using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// 从本地 appmanifest 读游戏版本。它是"游戏是否更新过"的权威信号，用来判断真映射模式
/// 记录的原始备份是否还有效——文件哈希分不清"我写的"和"别人写的"。
/// </summary>
public sealed class SteamAppManifestTests
{
	const string Sample = """
		"AppState"
		{
			"appid"		"553850"
			"name"		"HELLDIVERS 2"
			"StateFlags"		"4"
			"LastUpdated"		"1792675104"
			"SizeOnDisk"		"24745110319"
			"buildid"		"26088801"
			"TargetBuildID"		"26088801"
		}
		""";

	[Fact]
	public void Parse_ReadsBuildIdStateFlagsAndTimestamp()
	{
		var manifest = SteamAppManifest.Parse(Sample);
		Assert.NotNull(manifest);
		Assert.Equal("26088801", manifest!.BuildId);
		Assert.Equal(4, manifest.StateFlags);
		Assert.Equal(1792675104L, manifest.LastUpdatedUnix);
		Assert.True(manifest.IsFullyInstalled);
		Assert.Equal("26088801", manifest.TrustedBuildId);
	}

	[Fact]
	public void Parse_RefusesToTrustBuildIdWhileUpdating()
	{
		// StateFlags 不含"完整安装"位时，版本号可能正处于中间态，不能当判据。
		var manifest = SteamAppManifest.Parse(Sample.Replace("\"StateFlags\"\t\t\"4\"", "\"StateFlags\"\t\t\"1026\""));
		Assert.NotNull(manifest);
		Assert.False(manifest!.IsFullyInstalled);
		Assert.Null(manifest.TrustedBuildId);
	}

	[Fact]
	public void Parse_ReturnsNullWhenTheFileIsNotAManifest()
	{
		Assert.Null(SteamAppManifest.Parse("not a manifest"));
		Assert.Null(SteamAppManifest.Parse(string.Empty));
	}

	[Fact]
	public void ResolveManifestPath_WalksUpFromTheDataDirectory()
	{
		var path = SteamAppManifest.ResolveManifestPath(@"D:\SteamLibrary\steamapps\common\Helldivers 2\data");
		Assert.Equal(@"D:\SteamLibrary\steamapps\appmanifest_553850.acf", path);
	}

	[Fact]
	public void TryRead_ReturnsNullForAMissingFile()
	{
		Assert.Null(SteamAppManifest.TryRead(Path.Combine(Path.GetTempPath(), "no-such-manifest-" + Guid.NewGuid().ToString("N") + ".acf")));
	}

	[Fact]
	public void ClearBaseline_RemovesBackupAndState()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2-baseline-" + Guid.NewGuid().ToString("N"));
		var backup = Path.Combine(root, "backup");
		Directory.CreateDirectory(backup);
		File.WriteAllText(Path.Combine(backup, ArmorRegistryDataMapper.StateFileName), "x");
		File.WriteAllText(Path.Combine(backup, ArmorRegistryDataMapper.BackupFileName), "y");
		try
		{
			ArmorRegistryDataMapper.ClearBaseline(root);
			Assert.False(File.Exists(Path.Combine(backup, ArmorRegistryDataMapper.StateFileName)));
			Assert.False(File.Exists(Path.Combine(backup, ArmorRegistryDataMapper.BackupFileName)));
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}
}
