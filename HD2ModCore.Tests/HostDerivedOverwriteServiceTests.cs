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
		var root = CreateTemporaryDirectory();
		try
		{
			var host = CreateHost();
			var overwrite = Path.Combine(root, host.RelativePath, "Overwrite");
			Directory.CreateDirectory(overwrite);
			File.WriteAllText(Path.Combine(overwrite, DecorationHostRebuildService.DecorationOverwriteMarkerName), "generated");

			var service = CoreServices.CreateHostDerivedOverwriteService(new ThrowingDecorationRebuild(), new ThrowingHelmetMerge());
			var result = await service.RebuildAsync(new HostDerivedOverwriteRequest(host, [], false, root, "unused"));

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

			var service = CoreServices.CreateHostDerivedOverwriteService(new ThrowingDecorationRebuild(), new ThrowingHelmetMerge());
			await Assert.ThrowsAsync<InvalidOperationException>(
				async () => await service.RebuildAsync(new HostDerivedOverwriteRequest(host, [], false, root, "unused")));

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

	private sealed class ThrowingDecorationRebuild : IDecorationHostRebuildService
	{
		public Task<DecorationHostRebuildResult> RebuildHostAsync(
			ModNode host,
			IEnumerable<DecorationHostRebuildAttachment> enabled,
			string modsRoot,
			string gameDataDirectory,
			CancellationToken cancellationToken = default,
			IProgress<DecorationOperationProgress>? progress = null)
			=> throw new Xunit.Sdk.XunitException("No-decoration path must not invoke the decoration rebuild service.");
	}

	private sealed class ThrowingHelmetMerge : IHelmetToArmorMergeService
	{
		public ValueTask<HelmetToArmorMergeInspection> InspectAsync(ModNode source, string modsRootDirectory, string gameDataDirectory, CancellationToken cancellationToken = default)
			=> ValueTask.FromException<HelmetToArmorMergeInspection>(new Xunit.Sdk.XunitException("No-helmet path must not inspect helmet candidates."));

		public ValueTask<HelmetToArmorMergeResult> ExecuteAsync(ModNode source, string sourceDirectory, string outputDirectory, string gameDataDirectory, IReadOnlyCollection<HelmetToArmorMergeCandidate>? selectedCandidates = null, CancellationToken cancellationToken = default, IProgress<HelmetToArmorMergeProgress>? progress = null)
			=> ValueTask.FromException<HelmetToArmorMergeResult>(new Xunit.Sdk.XunitException("No-helmet path must not execute helmet merging."));
	}
}
