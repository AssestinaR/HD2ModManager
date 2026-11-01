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
	public void Mapping_AlwaysKeepsTrueMappingAndExternal()
	{
		foreach (var enabled in new[] { true, false })
		{
			var options = ArmorMappingModes.AvailableFor(enabled);
			Assert.Contains(ArmorMappingMode.TrueMapping, options);
			Assert.Contains(ArmorMappingMode.ExternalLuaEntry, options);
		}
	}

	[Fact]
	public void Physics_WhenBuiltInDisabledHasExactlyOneOption()
	{
		// 用户原话：「如果没有启用内置 lua，那么它的选项总是只有一个外置 lua 兼容模式」
		var options = PhysicsModes.AvailableFor(false);
		Assert.Single(options);
		Assert.Equal(PhysicsMode.ExternalLuaEntry, options[0]);
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
		// 保留"用运行时"这层意图，不跳到语义完全不同的真映射。
		Assert.Equal(
			ArmorMappingMode.ExternalLuaEntry,
			ArmorMappingModes.Normalize((int)ArmorMappingMode.ExclusiveLuaEntry, builtInLuaEnabled: false));
		// 真映射不受影响。
		Assert.Equal(
			ArmorMappingMode.TrueMapping,
			ArmorMappingModes.Normalize((int)ArmorMappingMode.TrueMapping, builtInLuaEnabled: false));
	}

	[Fact]
	public void Physics_AnyStoredValueBecomesExternalWhenBuiltInDisabled()
	{
		foreach (var stored in new[] { PhysicsMode.Off, PhysicsMode.ExclusiveLuaEntry, PhysicsMode.ExternalLuaEntry })
		{
			Assert.Equal(
				PhysicsMode.ExternalLuaEntry,
				PhysicsModes.Normalize((int)stored, builtInLuaEnabled: false));
		}
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
	public void Physics_DefaultIsOff()
	{
		// 发布初版时不能替用户打开物理：那是额外运行时开销，必须主动选。
		Assert.Equal(PhysicsMode.Off, PhysicsModes.Default);
	}

	[Fact]
	public void OnlyExclusiveModesOccupyBoot()
	{
		Assert.True(PhysicsMode.ExclusiveLuaEntry.OccupiesBoot());
		Assert.False(PhysicsMode.ExternalLuaEntry.OccupiesBoot());
		Assert.False(PhysicsMode.Off.OccupiesBoot());
	}
}
