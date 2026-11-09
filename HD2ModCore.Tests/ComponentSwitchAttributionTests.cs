using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// 「这个开关包属于哪个模型主体」的判据（按材质裸名求交）。
/// <para>
/// 为什么需要它：一次导入可能是 8 人替换包 —— 8 个模型、一批共同材质、一批各自的开关包。
/// 开关包现在会跟着 Mod 一起入库（重构建要靠它重新生成 section 开关表），所以"拷给谁"
/// 必须判得准：全塞给每个模型，会让 7 个模型各背上一串"要隐藏的材质找不到"的死开关。
/// </para>
/// </summary>
public sealed class ComponentSwitchAttributionTests
{
	private static ComponentSwitchPack Pack(string name, params string[] stems)
		=> new(name + ".vpk", name, stems);

	[Fact]
	public void 材质有交集就归属()
	{
		var packs = new[] { Pack("猫耳", "c_white_a_ear"), Pack("披风", "orange_mantle") };

		var owned = ComponentSwitchAttribution.ForTarget(packs, ["plum_body", "c_white_a_ear"]);

		var pack = Assert.Single(owned);
		Assert.Equal("猫耳", pack.DisplayName);
	}

	[Fact]
	public void 材质没有交集就不归属()
	{
		var packs = new[] { Pack("猫耳", "c_white_a_ear") };

		Assert.Empty(ComponentSwitchAttribution.ForTarget(packs, ["amber_body", "amber_hair"]));
	}

	[Fact]
	public void 裸名比较忽略目录扩展名与大小写()
	{
		// 开关包里是 `opt/c_white_a_ear.vmt` 这种影子路径；模型材质的裸名是 `c_white_a_ear`。
		var packs = new[] { Pack("猫耳", @"opt\C_White_A_Ear.vmt") };

		var owned = ComponentSwitchAttribution.ForTarget(packs, ["C_WHITE_A_EAR.vmt"]);

		Assert.Single(owned);
	}

	[Fact]
	public void 主体一个材质都没有时不带走任何开关()
	{
		var packs = new[] { Pack("猫耳", "c_white_a_ear") };

		Assert.Empty(ComponentSwitchAttribution.ForTarget(packs, []));
	}

	[Fact]
	public void 一个开关包可以被多个主体带走()
	{
		// 共用的开关包（同一批材质被两个模型引用）应当两个都带上，而不是先到先得。
		var packs = new[] { Pack("外套", "shared_coat") };

		Assert.Single(ComponentSwitchAttribution.ForTarget(packs, ["shared_coat", "a_body"]));
		Assert.Single(ComponentSwitchAttribution.ForTarget(packs, ["shared_coat", "b_body"]));
	}
}
