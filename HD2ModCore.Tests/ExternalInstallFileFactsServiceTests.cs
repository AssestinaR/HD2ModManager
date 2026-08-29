using System.Security.Cryptography;
using System.Text;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

public sealed class ExternalInstallFileFactsServiceTests
{
	[Theory]
	[InlineData("Plugin.addon64", "Plugin.addon64")]
	[InlineData("plugin.ADDON", "plugin.ADDON")]
	[InlineData("bin/Plugins/Plugin.addon64", "Plugins/Plugin.addon64")]
	[InlineData("BIN\\Plugin.addon", "Plugin.addon")]
	public void TryMap_RecognizesRootAndExplicitBinFiles(string source, string target)
	{
		Assert.True(ExternalInstallPathMapper.TryMap(source, out var root, out var normalizedSource, out var targetPath));
		Assert.Equal(ExternalInstallTargetRoot.GameBin, root);
		Assert.Equal(source.Replace('\\', '/'), normalizedSource);
		Assert.Equal(target, targetPath);
	}

	[Theory]
	[InlineData("nested/Plugin.addon64")]
	[InlineData("bin/Plugin.dll")]
	[InlineData("Plugin.dll")]
	[InlineData("../Plugin.addon64")]
	[InlineData("C:/Plugin.addon64")]
	public void TryMap_RejectsUnknownOrUnsafePaths(string source)
	{
		Assert.False(ExternalInstallPathMapper.TryMap(source, out _, out _, out _));
	}

	[Fact]
	public void NormalizeRelativePath_UsesForwardSlashesAndRejectsTraversal()
	{
		Assert.True(ExternalInstallPathMapper.TryNormalizeRelativePath("./bin\\plugins/./x.addon64", out var normalized));
		Assert.Equal("bin/plugins/x.addon64", normalized);
		Assert.False(ExternalInstallPathMapper.TryNormalizeRelativePath("bin/../x.addon64", out _));
		Assert.False(ExternalInstallPathMapper.TryNormalizeRelativePath("\\server\\share\\x.addon64", out _));
	}

	[Fact]
	public void ConflictKey_IsStableAcrossSeparatorsAndCase()
	{
		var left = ExternalInstallPathMapper.CreateConflictKey(ExternalInstallTargetRoot.GameBin, "Plugins\\Example.addon64");
		var right = ExternalInstallPathMapper.CreateConflictKey(ExternalInstallTargetRoot.GameBin, "plugins/example.ADDON64");
		Assert.Equal(left, right);
		Assert.Equal("GameBin:PLUGINS/EXAMPLE.ADDON64", left);
	}

	[Fact]
	public async Task DetectAsync_ComputesHash_MapsBin_AndIgnoresDll()
	{
		var root = CreateTempDirectory();
		try
		{
			var rootAddon = Path.Combine(root, "Root.addon64");
			var binAddon = Path.Combine(root, "bin", "Nested", "Bin.addon");
			var dll = Path.Combine(root, "bin", "Ignored.dll");
			Directory.CreateDirectory(Path.GetDirectoryName(binAddon)!);
			await File.WriteAllTextAsync(rootAddon, "root-addon", Encoding.UTF8);
			await File.WriteAllTextAsync(binAddon, "bin-addon", Encoding.UTF8);
			await File.WriteAllTextAsync(dll, "not-a-plugin", Encoding.UTF8);

			var result = await new ExternalInstallFileFactsService().DetectAsync(root);

			Assert.Equal(2, result.Files.Count);
			Assert.Contains(result.Files, fact => fact.SourceRelativePath == "Root.addon64"
				&& fact.TargetRoot == ExternalInstallTargetRoot.GameBin
				&& fact.TargetRelativePath == "Root.addon64"
				&& fact.Kind == ExternalInstallFileKind.ReShadePlugin
				&& fact.Length == new FileInfo(rootAddon).Length
				&& fact.Sha256 == Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("root-addon"))).ToLowerInvariant());
			Assert.Contains(result.Files, fact => fact.SourceRelativePath == "bin/Nested/Bin.addon"
				&& fact.TargetRelativePath == "Nested/Bin.addon");
			Assert.Contains("bin/Ignored.dll", result.UnrecognizedExternalPaths);
			Assert.DoesNotContain(result.Files, fact => fact.SourceRelativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task DetectAsync_ReportsTargetConflicts()
	{
		var root = CreateTempDirectory();
		try
		{
			Directory.CreateDirectory(Path.Combine(root, "bin"));
			await File.WriteAllTextAsync(Path.Combine(root, "Same.addon64"), "one");
			await File.WriteAllTextAsync(Path.Combine(root, "bin", "Same.addon64"), "two");

			var result = await new ExternalInstallFileFactsService().DetectAsync(root);

			var conflict = Assert.Single(result.Conflicts);
			Assert.Equal("GameBin:SAME.ADDON64", conflict.ConflictKey);
			Assert.Equal(2, conflict.Files.Count);
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	private static string CreateTempDirectory()
	{
		var path = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(path);
		return path;
	}
}
