using HD2ModCore.Domain;

namespace HD2ModCore.Tests;

/// <summary>
/// 「内置 lua」总开关对两个下拉选项的约束。
///
/// 这是产品契约，不是实现细节：**总开关关闭时，两个下拉里都不能出现「独占」**——
/// 选项本身表达能力边界，而不是让用户选完再发现无效（用户 2026-11-01 明确要求）。
/// </summary>
public sealed class BuiltInLuaGatingTests
{
	[Fact]
	public void Mapping_OffersExclusiveOnlyWhenBuiltInLuaIsEnabled()
	{
		Assert.Contains(ArmorMappingMode.ExclusiveLuaEntry, ArmorMappingModes.AvailableFor(true));
		Assert.DoesNotContain(ArmorMappingMode.ExclusiveLuaEntry, ArmorMappingModes.AvailableFor(false));
	}

	[Fact]
	public void Mapping_OffersExactlyThreeModesLikePhysics()
	{
		// 用户口径（2026-11-11）：软映射的选项要与物理一致——关闭 / 独占 boot / 外置加载器兼容。
		var enabled = ArmorMappingModes.AvailableFor(true);
		Assert.Equal(3, enabled.Count);
		Assert.Contains(ArmorMappingMode.ExclusiveLuaEntry, enabled);
		Assert.Contains(ArmorMappingMode.ExternalLuaEntry, enabled);
		Assert.Contains(ArmorMappingMode.Off, enabled);

		// 数值与物理逐档对齐：0=关闭、1=独占、2=外置。
		Assert.Equal((int)PhysicsMode.Off, (int)ArmorMappingMode.Off);
		Assert.Equal((int)PhysicsMode.ExclusiveLuaEntry, (int)ArmorMappingMode.ExclusiveLuaEntry);
		Assert.Equal((int)PhysicsMode.ExternalLuaEntry, (int)ArmorMappingMode.ExternalLuaEntry);

		// 显示名也要一致，否则并排两个下拉会像在说两回事。
		foreach (var mode in enabled)
			Assert.Equal(((PhysicsMode)(int)mode).DisplayName(), mode.DisplayName());
	}

	[Fact]
	public void Mapping_LegacyStoredValuesAreTranslatedNotReinterpreted()
	{
		// **这是升级时最容易出错的一处。** 旧口径 0=独占 / 1=外置 / 2=真映射，
		// 新口径 0=关闭 / 1=独占 / 2=外置。直接把旧值当新值读，会把"开着独占"变成"关闭"。
		Assert.Equal(ArmorMappingMode.ExclusiveLuaEntry, ArmorMappingModes.MigrateStoredValue(0, schema: 0));
		Assert.Equal(ArmorMappingMode.ExternalLuaEntry, ArmorMappingModes.MigrateStoredValue(1, schema: 0));
		// 真映射已停用 ⇒ 落到「关闭」（同样不投放运行时，是最接近的替代）。
		Assert.Equal(ArmorMappingMode.Off, ArmorMappingModes.MigrateStoredValue(2, schema: 0));

		// 新口径的存档不做翻译，原样读。
		Assert.Equal(ArmorMappingMode.Off, ArmorMappingModes.MigrateStoredValue(0, ArmorMappingModes.CurrentSchema));
		Assert.Equal(ArmorMappingMode.ExclusiveLuaEntry, ArmorMappingModes.MigrateStoredValue(1, ArmorMappingModes.CurrentSchema));
		Assert.Equal(ArmorMappingMode.ExternalLuaEntry, ArmorMappingModes.MigrateStoredValue(2, ArmorMappingModes.CurrentSchema));
	}

	[Fact]
	public void Physics_WhenBuiltInDisabledOffersExternalAndOff()
	{
		// 早先口径是「总开关关时只有外置」——那时外置包要玩家手动加进库，不加就等于关。
		// 2026-11-14（规划 kit-instance-mapping-deploy-plan §15.2）外置改成部署自动投放，必须能选「关闭」，与映射一致。
		var options = PhysicsModes.AvailableFor(false);
		Assert.Equal([PhysicsMode.ExternalLuaEntry, PhysicsMode.Off], options);
	}

	[Fact]
	public void Physics_WhenBuiltInEnabledOffersAllThree()
	{
		var options = PhysicsModes.AvailableFor(true);
		Assert.Equal(3, options.Count);
		Assert.Contains(PhysicsMode.ExclusiveLuaEntry, options);
		Assert.Contains(PhysicsMode.ExternalLuaEntry, options);
		Assert.Contains(PhysicsMode.Off, options);
	}

	[Fact]
	public void Mapping_ExclusiveCollapsesToExternalWhenBuiltInDisabled()
	{
		// 保留"用运行时"这层意图，不跳到「关闭」把功能悄悄关掉。
		Assert.Equal(
			ArmorMappingMode.ExternalLuaEntry,
			ArmorMappingModes.Normalize((int)ArmorMappingMode.ExclusiveLuaEntry, builtInLuaEnabled: false));
		// 其余两档不受总开关影响。
		Assert.Equal(
			ArmorMappingMode.ExternalLuaEntry,
			ArmorMappingModes.Normalize((int)ArmorMappingMode.ExternalLuaEntry, builtInLuaEnabled: false));
		Assert.Equal(
			ArmorMappingMode.Off,
			ArmorMappingModes.Normalize((int)ArmorMappingMode.Off, builtInLuaEnabled: false));
	}

	[Fact]
	public void Physics_ExclusiveCollapsesToExternalWhenBuiltInDisabled_OffStaysOff()
	{
		Assert.Equal(PhysicsMode.ExternalLuaEntry, PhysicsModes.Normalize((int)PhysicsMode.ExclusiveLuaEntry, builtInLuaEnabled: false));
		Assert.Equal(PhysicsMode.ExternalLuaEntry, PhysicsModes.Normalize((int)PhysicsMode.ExternalLuaEntry, builtInLuaEnabled: false));
		Assert.Equal(PhysicsMode.Off, PhysicsModes.Normalize((int)PhysicsMode.Off, builtInLuaEnabled: false));
	}

	[Fact]
	public void Physics_NormalizeKeepsValueWhenBuiltInEnabled()
	{
		foreach (var stored in new[] { PhysicsMode.Off, PhysicsMode.ExclusiveLuaEntry, PhysicsMode.ExternalLuaEntry })
		{
			Assert.Equal(stored, PhysicsModes.Normalize((int)stored, builtInLuaEnabled: true));
		}
		// 未知值回落到默认（关闭）。
		Assert.Equal(PhysicsModes.Default, PhysicsModes.Normalize(999, builtInLuaEnabled: true));
	}

	[Fact]
	public void Defaults_AreOffForBothMappingAndPhysics()
	{
		// 2026-11-11：映射改为三档后默认「关闭」，与物理一致——这两项都是"替用户改外观/加运行时"
		// 的功能，默认不替用户开启。（此前默认真映射，那一档已停用。）
		Assert.Equal(ArmorMappingMode.Off, ArmorMappingModes.Default);
		// 物理**保持关闭**——2026-11-03 试过改成「独占 boot」，随即改回来：物理要读游戏自己的
		// boot 归档，默认开等于把一个"可能拿不到数据就失败"的分支变成默认路径。
		Assert.Equal(PhysicsMode.Off, PhysicsModes.Default);
	}

	[Fact]
	public void Defaults_AreSelectableWithoutTouchingBuiltInLuaSwitch()
	{
		// 默认值必须在选项里出现，否则设置页一打开就显示一个下拉里不存在的值。
		// 「关闭」在两态下都提供，所以映射的默认值在总开关两态下都在。
		Assert.Contains(ArmorMappingModes.Default, ArmorMappingModes.AvailableFor(true));
		Assert.Contains(ArmorMappingModes.Default, ArmorMappingModes.AvailableFor(false));
		Assert.Contains(PhysicsModes.Default, PhysicsModes.AvailableFor(true));
		// 外置自动投放后（§15.2），物理的「关闭」在总开关关闭时也在选项里。
		Assert.Contains(PhysicsModes.Default, PhysicsModes.AvailableFor(false));
	}

	[Fact]
	public void OnlyExclusiveModesOccupyBoot()
	{
		Assert.True(PhysicsMode.ExclusiveLuaEntry.OccupiesBoot());
		Assert.False(PhysicsMode.ExternalLuaEntry.OccupiesBoot());
		Assert.False(PhysicsMode.Off.OccupiesBoot());
	}
}
