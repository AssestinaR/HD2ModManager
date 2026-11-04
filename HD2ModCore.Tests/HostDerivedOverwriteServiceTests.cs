using HD2ModCore.Application;
using HD2ModCore.Decoration;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

public sealed class HostDerivedOverwriteServiceTests
{
	[Fact]
	public async Task RebuildAsync_NoDerivedLayers_RemovesOnlyManagedOverwrite()
	{
		// 用**装饰**那枚旧标记造一个托管 Overwrite：装饰虽已退场，但用户库里可能还留着
		// 当年生成的目录。服务必须仍然认得它、并且在不需要派生层时把它清掉——
		// 认不出来就会把它当成用户手工文件而拒绝动手，那些残留就永远清不掉。
		var root = CreateTemporaryDirectory();
		try
		{
			var host = CreateHost();
			var overwrite = Path.Combine(root, host.RelativePath, "Overwrite");
			Directory.CreateDirectory(overwrite);
			File.WriteAllText(Path.Combine(overwrite, ManagedOverwriteMarkers.Decoration), "generated");

			var service = CoreServices.CreateHostDerivedOverwriteService(new ThrowingHelmetMerge());
			var result = await service.RebuildAsync(new HostDerivedOverwriteRequest(host, false, root, "unused"));

			Assert.True(result.Succeeded);
			Assert.True(result.ClearedManagedOverwrite);
			Assert.False(Directory.Exists(overwrite));
		}
		finally
		{
			DeleteTemporaryDirectory(root);
		}
	}

	[Fact]
	public async Task RebuildAsync_NoDerivedLayers_RefusesUnknownOverwrite()
	{
		var root = CreateTemporaryDirectory();
		try
		{
			var host = CreateHost();
			var overwrite = Path.Combine(root, host.RelativePath, "Overwrite");
			Directory.CreateDirectory(overwrite);
			File.WriteAllText(Path.Combine(overwrite, "user-file.txt"), "keep");

			var service = CoreServices.CreateHostDerivedOverwriteService(new ThrowingHelmetMerge());
			await Assert.ThrowsAsync<InvalidOperationException>(
				async () => await service.RebuildAsync(new HostDerivedOverwriteRequest(host, false, root, "unused")));

			Assert.True(File.Exists(Path.Combine(overwrite, "user-file.txt")));
		}
		finally
		{
			DeleteTemporaryDirectory(root);
		}
	}

	private static ModNode CreateHost()
		=> new(
			ModNodeId.New(),
			"host",
			new ModNodeMetadata("Host", null, DateTimeOffset.UtcNow, null),
			[],
			[]);

	private static string CreateTemporaryDirectory()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2-host-derived-overwrite-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		return root;
	}

	private static void DeleteTemporaryDirectory(string root)
	{
		if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
	}

	private sealed class ThrowingHelmetMerge : IHelmetToArmorMergeService
	{
		public ValueTask<HelmetToArmorMergeInspection> InspectAsync(ModNode source, string modsRootDirectory, string gameDataDirectory, CancellationToken cancellationToken = default)
			=> ValueTask.FromException<HelmetToArmorMergeInspection>(new Xunit.Sdk.XunitException("No-helmet path must not inspect helmet candidates."));

		public ValueTask<HelmetToArmorMergeResult> ExecuteAsync(ModNode source, string sourceDirectory, string outputDirectory, string gameDataDirectory, IReadOnlyCollection<HelmetToArmorMergeCandidate>? selectedCandidates = null, CancellationToken cancellationToken = default, IProgress<HelmetToArmorMergeProgress>? progress = null)
			=> ValueTask.FromException<HelmetToArmorMergeResult>(new Xunit.Sdk.XunitException("No-helmet path must not execute helmet merging."));

		public ValueTask<HelmetToArmorMergeResult> ExecuteAsync(ModNode source, string sourceDirectory, string outputDirectory, string gameDataDirectory, IReadOnlyCollection<HelmetToArmorMergeCandidate>? selectedCandidates, CancellationToken cancellationToken, IProgress<HelmetToArmorMergeProgress>? progress, HelmetToArmorMergeOutputMode outputMode)
			=> ValueTask.FromException<HelmetToArmorMergeResult>(new Xunit.Sdk.XunitException("No-helmet path must not execute helmet merging."));
	}
}
