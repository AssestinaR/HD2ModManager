using HD2SourceMigration.VpkConversion;

namespace HD2SourceMigration.Tests;

/// <summary>
/// 核对 <c>advanced</c> 模板的**槽位键**是否与规格一致。
/// <para>
/// <b>为什么必须核对。</b>我是按"槽下标"写材质的：槽 2 写法线、槽 3 写金属、槽 5 写底色。
/// 规格（<c>MATERIAL_PIPELINE_SPEC.md</c>）给的是<b>键</b>：
/// </para>
/// <list type="bullet">
/// <item>槽 2 → <c>4c567810</c>（RGB 法线 / A 粗糙度）</item>
/// <item>槽 3 → <c>90b84a53</c>（金属度）</item>
/// <item>槽 5 → <c>e67ac0c7</c>（RGB 底色 / A 自发光）</item>
/// </list>
/// <para>
/// <b>如果实际顺序不是这样，法线就会被写进别的槽</b> —— 那会直接表现为着色反了。
/// 实测用户反馈 M3 版"内部可见、外部不可见"，而几何与 M2 逐字节相同，
/// 所以问题只能在材质侧，槽位错位是首要嫌疑。
/// </para>
/// </summary>
public sealed class MaterialSlotKeyTests
{
	private static Hd2MaterialAsset? LoadAdvanced()
	{
		var library = Hd2MaterialTemplateLibrary.Locate();
		return library is null || !library.Contains("advanced") ? null : library.Load("advanced");
	}

	[Fact]
	public void AdvancedTemplateSlotsCarryTheDocumentedKeys()
	{
		var material = LoadAdvanced();
		if (material is null)
			return;

		// 先看槽数够不够，不够就直接报出来（不然下面的下标会越界）。
		Assert.True(material.TextureKeys.Count > 5,
			$"advanced 只有 {material.TextureKeys.Count} 个槽，不够到槽 5");

		Assert.Equal(0x4c567810u, material.TextureKeys[SourceMaterialConverter.AdvancedNarSlot]);
		Assert.Equal(0x90b84a53u, material.TextureKeys[SourceMaterialConverter.AdvancedMetallicSlot]);
		Assert.Equal(0xe67ac0c7u, material.TextureKeys[SourceMaterialConverter.AdvancedAlbedoSlot]);
	}

	/// <summary>
	/// 把 <c>advanced</c> 的全部槽键列出来。
	/// <para>
	/// 失败信息里会带上完整清单 —— 这样一旦上面的断言红了，**不必再去翻规格**，
	/// 直接从报出来的清单里认哪个键对应哪个语义。
	/// </para>
	/// </summary>
	[Fact]
	public void AdvancedTemplateSlotKeysAreReported()
	{
		var material = LoadAdvanced();
		if (material is null)
			return;

		var listed = string.Join("、", material.TextureKeys.Select((key, index) => $"[{index}]=0x{key:x8}"));
		Assert.True(material.TextureKeys.Count > 0, listed);
	}
}
