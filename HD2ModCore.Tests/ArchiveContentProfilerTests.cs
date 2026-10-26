using HD2ModAdaptation.Analysis;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// 内容域推断：权威与实体百科都覆盖不到的 archive，靠内容路径补一个域。
/// 这里钉的是"只填空、不覆盖、不硬猜"这三条边界。
/// </summary>
public sealed class ArchiveContentProfilerTests
{
	private static readonly ulong UnitType = PatchUnitMeshReader.UnitTypeId;
	private const ulong TextureType = 0xcd4238c6a0c69e32;

	[Fact]
	public void Profile_UsesMostVotedContentPath()
	{
		var entries = new[]
		{
			Entry(UnitType, 1),
			Entry(UnitType, 2),
			Entry(TextureType, 3),
		};
		var names = new Dictionary<ulong, string>
		{
			[1] = "content/env_super_earth/habs/hab_01",
			[2] = "content/env_super_earth/habs/hab_02",
			[3] = "content/art_shared/textures/atlas",
		};

		var profile = ArchiveContentProfiler.Profile(entries, names);

		Assert.NotNull(profile);
		Assert.Equal("Environment", profile!.Domain);
		Assert.Equal("env_super_earth", profile.Subject);
		Assert.Equal("env_super_earth/habs", profile.DerivedName);
		Assert.True(profile.HasUsableName);
		Assert.Equal(ArchiveContentProfiler.DerivedConfidence, profile.Confidence);
	}

	[Fact]
	public void Profile_IgnoresPlainResourceNames()
	{
		// "GL-21 Grenade Launcher" 是资源名，不是内容分类——拿它当域会把武器名变成分类。
		var entries = new[] { Entry(UnitType, 1) };
		var names = new Dictionary<ulong, string> { [1] = "GL-21 Grenade Launcher" };

		var profile = ArchiveContentProfiler.Profile(entries, names);

		Assert.NotNull(profile);
		Assert.Equal("UnitBundle", profile!.Domain);
		Assert.False(profile.HasUsableName);
	}

	[Fact]
	public void Profile_FallsBackToCompositionWithoutNames()
	{
		var textureOnly = ArchiveContentProfiler.Profile([Entry(TextureType, 1)], new Dictionary<ulong, string>());
		Assert.Equal("TexturePack", textureOnly!.Domain);

		var empty = ArchiveContentProfiler.Profile([], new Dictionary<ulong, string>());
		Assert.Equal("Empty", empty!.Domain);

		var units = ArchiveContentProfiler.Profile(
			[Entry(UnitType, 1), Entry(UnitType, 2), Entry(UnitType, 3)],
			new Dictionary<ulong, string>());
		Assert.Equal("UnitBundle", units!.Domain);
		Assert.Equal("3 unit", units.Subject);
		// 构成兜底的名字不唯一，不能拿去当显示名。
		Assert.False(units.HasUsableName);
	}

	private static GameDataArchiveEntryFact Entry(ulong typeId, ulong fileId)
		=> new(
			new AssetKey(typeId, fileId),
			"archive",
			0,
			0,
			0,
			0,
			0,
			0,
			0,
			0,
			0,
			0,
			0);
}
