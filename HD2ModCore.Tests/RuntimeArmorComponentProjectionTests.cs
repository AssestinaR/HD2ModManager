using HD2ModCore.Domain;

namespace HD2ModCore.Tests;

public sealed class RuntimeArmorComponentProjectionTests
{
	[Fact]
	public void FollowUsesSourceUnitsAndOverridesNotTargets()
	{
		RuntimeArmorRegistryModelPiece[] source = [new(0, "00000000000000ab", 2, 0)];
		RuntimeArmorRegistryPieceOverride[] overrides =
		[
			new(10, 0, "00000000000000AB", 2, 0, false),
			new(20, 0, "00000000000000ab", 2, 0, true)
		];
		var followed = RuntimeArmorComponentProjection.Project(source, 10, 20, true, overrides);
		Assert.False(Assert.Single(followed).Enabled);
		Assert.Equal(source[0].Unit, followed[0].Unit);
		Assert.True(Assert.Single(RuntimeArmorComponentProjection.Project(source, 10, 20, false, overrides)).Enabled);
		Assert.False(Assert.Single(RuntimeArmorComponentProjection.Project(source, 10, 20, true, overrides)).Enabled);
	}

	[Fact]
	public void CustomSourceStructureAndDisabledDefinitionArePreserved()
	{
		RuntimeArmorRegistryModelPiece[] source =
		[
			new(3, "0000000000000001", 6, 2, Enabled: false),
			new(0, "0000000000000002", 2, 0),
			new(1, "0000000000000003", 2, 0, IsReserved: true)
		];
		RuntimeArmorRegistryPieceOverride[] overrides = [new(20, 3, source[0].Unit, 6, 2, true)];
		var result = RuntimeArmorComponentProjection.Project(source, null, 20, false, overrides);
		Assert.Equal(3, result.Count);
		Assert.False(result[0].Enabled);
		Assert.True(result[2].IsReserved);
		Assert.Equal(source, RuntimeArmorComponentProjection.Project(source, null, 20, true, overrides));
	}

	[Fact]
	public void ManualOverridesAreScopedToTargetAndCompletePieceKey()
	{
		RuntimeArmorRegistryModelPiece[] source = [new(0, "0000000000000001", 2, 0)];
		RuntimeArmorRegistryPieceOverride[] overrides =
		[
			new(21, 0, source[0].Unit, 2, 0, false),
			new(20, 1, source[0].Unit, 2, 0, false),
			new(20, 0, source[0].Unit, 2, 1, false),
			new(20, 0, "0000000000000002", 2, 0, false)
		];
		Assert.True(Assert.Single(RuntimeArmorComponentProjection.Project(source, 10, 20, false, overrides)).Enabled);
	}
}
