using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using Xunit;

namespace HD2ModAdaptation.Tests;

public sealed class UnitStreamAbiProfilesTests
{
	[Fact]
	public void Resolve_10800436_MapsCompleteLegacySkinningContract()
	{
		var resolution = Resolve(10800436u);

		Assert.Equal(UnitStreamAbiResolutionPath.ExactVersionProfile, resolution.Path);
		Assert.Equal(UnitStreamAbiProfileKind.LegacyPre10800438, resolution.Profile);
		Assert.Equal("vec3_float", UnitStreamAbiProfiles.GetFormat(resolution, 2).Name);
		Assert.Equal("unk_normal", UnitStreamAbiProfiles.GetFormat(resolution, 26).Name);
		Assert.Equal("vec2_half", UnitStreamAbiProfiles.GetFormat(resolution, 29).Name);
		Assert.Equal("vec4_half", UnitStreamAbiProfiles.GetFormat(resolution, 31).Name);
		Assert.Equal("vec4_uint8", UnitStreamAbiProfiles.GetFormat(resolution, 24).Name);
	}

	[Fact]
	public void Resolve_UnknownVersionWithLegacyFingerprint_UsesFallback()
	{
		var resolution = Resolve(10990001u);

		Assert.True(resolution.UsedFallback);
		Assert.Equal(UnitStreamAbiProfileKind.LegacyPre10800438, resolution.Profile);
	}

	[Fact]
	public void Resolve_UnknownFormat_RemainsUnresolvedInsteadOfGuessing()
	{
		var resolution = UnitStreamAbiProfiles.Resolve(10990002u,
		[
			new UnitStreamAbiRawStream(0, 12,
			[
				new UnitStreamAbiRawComponent(0, 999, 0, 0)
			])
		]);

		Assert.False(resolution.IsResolved);
		Assert.Equal(UnitStreamAbiResolutionPath.Unresolved, resolution.Path);
	}

	private static UnitStreamAbiResolution Resolve(uint version)
		=> UnitStreamAbiProfiles.Resolve(version,
		[
			new UnitStreamAbiRawStream(0, 32,
			[
				new UnitStreamAbiRawComponent(0, 2, 0, 0),
				new UnitStreamAbiRawComponent(1, 26, 0, 0),
				new UnitStreamAbiRawComponent(4, 29, 0, 0),
				new UnitStreamAbiRawComponent(7, 31, 0, 0),
				new UnitStreamAbiRawComponent(6, 24, 0, 0)
			])
		]);
}
