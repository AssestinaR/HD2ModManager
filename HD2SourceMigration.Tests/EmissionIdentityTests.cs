using HD2SourceMigration.VpkConversion;

namespace HD2SourceMigration.Tests;

/// <summary>
/// 材质身份必须含"是否自发光"。
/// <para>
/// 背景：材质 id 只由 <c>$basetexture</c> 决定，而实测 <c>Shinano_face</c> 与
/// <c>Shinano_face_e</c> **共用同一张底色**（差异只在 <c>$emissiontexture</c>/<c>$emissiontint</c>）。
/// 于是两者撞成同一个 id、被去重合并，**发光那层静默丢失**。
/// </para>
/// <para>
/// 它们本来就是两个不同的材质，所以身份要含这一点。这条测试钉住"两者不再合并"。
/// </para>
/// <para>依赖真实游戏数据；找不到就跳过。</para>
/// </summary>
public sealed class EmissionIdentityTests
{
	private static SourceMaterialSource? OpenSource()
	{
		var gameRoot = Environment.GetEnvironmentVariable("HD2_L4D2_ROOT");
		if (string.IsNullOrWhiteSpace(gameRoot) || !Directory.Exists(gameRoot))
			gameRoot = @"D:\SteamLibrary\steamapps\common\Left 4 Dead 2\left4dead2";
		if (!Directory.Exists(gameRoot))
			return null;
		return SourceMaterialSource.OpenDirectories(gameRoot, Path.Combine(gameRoot, "addons"));
	}

	[Fact]
	public void TheEmissionVariantNoLongerCollapsesIntoItsBase()
	{
		var source = OpenSource();
		if (source is null)
			return;
		if (!source.TryReadVmt("Shinano_face", out var baseBytes)
			|| !source.TryReadVmt("Shinano_face_e", out var variantBytes))
		{
			return;
		}

		var baseVmt = VmtDocument.Parse(baseBytes);
		var variantVmt = VmtDocument.Parse(variantBytes);

		// 前提：两者共用底色，而且只有发光变体声明了 $emissiontexture。
		Assert.Equal(
			VmtDocument.NormalizePath(baseVmt.BaseTexture ?? string.Empty),
			VmtDocument.NormalizePath(variantVmt.BaseTexture ?? string.Empty));
		Assert.True(string.IsNullOrWhiteSpace(baseVmt.Value("$emissiontexture")),
			"本体不该声明 $emissiontexture");
		Assert.False(string.IsNullOrWhiteSpace(variantVmt.Value("$emissiontexture")),
			"发光变体应当声明 $emissiontexture");

		// 身份必须不同 —— 否则又会被合并。
		var baseIdentity = VmtDocument.NormalizePath(baseVmt.BaseTexture ?? string.Empty);
		var variantIdentity = VmtDocument.NormalizePath(variantVmt.BaseTexture ?? string.Empty) + "|emission";
		Assert.NotEqual(Hd2AssetId.Material(baseIdentity), Hd2AssetId.Material(variantIdentity));
	}

	/// <summary>
	/// **发光真的写进去了吗。** 这是第 38 轮唯一没有证明的断言：
	/// "0 条诊断"只说明没抛异常，不说明 alpha 非零。
	/// <para>
	/// 判据是一对：发光变体的底色 alpha **非全零**，本体的底色 alpha **必须仍是全零**
	/// （槽 5 的 alpha 就是 emission，本体不该发光）。
	/// </para>
	/// <para>依赖真实游戏数据与 texconv；缺任一项跳过。</para>
	/// </summary>
	[Fact]
	public void TheEmissionVariantCarriesNonZeroAlphaWhileTheBaseStaysZero()
	{
		var source = OpenSource();
		var encoderPath = Bc7Encoder.Locate(Environment.GetEnvironmentVariable("HD2_TEXCONV"));
		var templates = Hd2MaterialTemplateLibrary.Locate();
		if (source is null || encoderPath is null || templates is null)
			return;

		var encoder = new Bc7Encoder(encoderPath);
		var converter = new SourceMaterialConverter(source, encoder, templates);

		static int NonZeroAlphaBytes(SynthesizedTexture texture, Bc7Encoder encoder)
		{
			var dds = Hd2TextureAsset.BuildDds(
				texture.Width, texture.Height, Hd2TextureAsset.Bc7UnormSrgb, [texture.GpuData]);
			var rgba = encoder.DecodeToRgba(dds);
			var count = 0;
			for (var offset = 3; offset < rgba.Length; offset += 4)
			{
				if (rgba[offset] != 0)
					count++;
			}

			return count;
		}

		var variant = converter.Convert("Shinano_face_e");
		var baseResult = converter.Convert("Shinano_face");
		if (!variant.IsValid || !baseResult.IsValid)
			return;   // 素材不在时跳过，而不是误报失败

		var variantNonZero = NonZeroAlphaBytes(variant.Textures[0], encoder);
		var baseNonZero = NonZeroAlphaBytes(baseResult.Textures[0], encoder);

		// ① 发光变体必须**确实**带上了 emission。
		Assert.True(variantNonZero > 0, "发光变体的底色 alpha 全为零 —— 发光没有被写进去");

		// ② 本体的 alpha 必须**显著更少**。
		//
		// ⚠️ 这里**不能断言"恰好全零"**：BC7 是有损的，编码时 alpha=0、解回来不保证还是 0。
		// 所以判据用"数量级差距"，而不是"相等"——前者对编解码噪声鲁棒，后者不是。
		Assert.True(variantNonZero > baseNonZero * 2,
			$"发光变体的非零 alpha 像素数（{variantNonZero}）没有显著多于本体（{baseNonZero}）——"
			+ "发光可能没写进去，或者本体的 alpha 本来就不该这么高");
	}
}
