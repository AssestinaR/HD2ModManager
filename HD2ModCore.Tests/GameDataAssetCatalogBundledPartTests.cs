using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

// 作用：验证 bundled 归档（bundles.nxa）下三段载荷各自用带后缀的包名去解析。
//       基名承载 TOC，`<基名>.stream` / `<基名>.gpu_resources` 承载另两段；
//       漏拼后缀会让 GPU/Stream 静默丢失（历史上 `gamedata extract asset` 只写出 .toc）。
// Purpose: Verifies each part of a bundled archive resolves under its own suffixed package name.
//          The bare name carries the TOC while the suffixed names carry stream and GPU; missing the
//          suffix silently dropped GPU/Stream (historically `gamedata extract asset` wrote only .toc).
public sealed class GameDataAssetCatalogBundledPartTests
{
	private const string ArchiveId = "d923906f8b815987";

	[Fact]
	public async Task ReadPartAsync_BundledStorage_ResolvesEachPartUnderItsOwnSuffixedPackageName()
	{
		var resolver = new RecordingResolver();
		var service = new GameDataAssetCatalogService(Path.GetTempPath(), resolver);
		var asset = CreateBundledAsset();

		var toc = await service.ReadPartAsync(asset, GameDataAssetPart.TocData);
		var stream = await service.ReadPartAsync(asset, GameDataAssetPart.Stream);
		var gpu = await service.ReadPartAsync(asset, GameDataAssetPart.GpuResources);

		// 三段必须各自命中不同的包条目：TOC 用基名，另两段必须带后缀。
		// Each part must hit a different package entry: the bare name for the TOC, suffixed names for the rest.
		Assert.Equal(
			new[] { ArchiveId, ArchiveId + ".stream", ArchiveId + ".gpu_resources" },
			resolver.RequestedNames);

		Assert.Equal(12, toc!.Length);
		Assert.Equal(8, stream!.Length);
		Assert.Equal(16, gpu!.Length);
	}

	[Fact]
	public async Task ReadPartAsync_BundledStorage_DoesNotReuseBareNameForGpuOrStream()
	{
		var resolver = new RecordingResolver();
		var service = new GameDataAssetCatalogService(Path.GetTempPath(), resolver);
		var asset = CreateBundledAsset();

		await service.ReadPartAsync(asset, GameDataAssetPart.GpuResources);

		// 负对照：GPU 段绝不能拿基名去查（那正是历史 bug 的形态）。
		// Negative control: the GPU part must never be looked up by the bare name (the historical bug).
		Assert.Equal(new[] { ArchiveId + ".gpu_resources" }, resolver.RequestedNames);
		Assert.DoesNotContain(ArchiveId, resolver.RequestedNames);
	}

	[Fact]
	public async Task ReadPartAsync_LegacyStorage_DoesNotRouteThroughPackageResolver()
	{
		// 回归保护：legacy 布局把三段放在并列文件里直接读，不经过包解析器；
		// 因此 bundled 的后缀修复不会影响它。
		// Regression guard: legacy layout reads sibling files directly and never goes through the
		// package resolver, so the bundled suffix fix cannot affect it.
		var resolver = new RecordingResolver();
		var service = new GameDataAssetCatalogService(Path.GetTempPath(), resolver);
		var asset = CreateBundledAsset() with { UsesBundledStorage = false };

		await service.ReadPartAsync(asset, GameDataAssetPart.GpuResources);

		Assert.Empty(resolver.RequestedNames);
	}

	private static GameDataAssetDescriptor CreateBundledAsset() => new(
		ArchiveId: ArchiveId,
		UsesBundledStorage: true,
		TypeId: 0xe0a48d0be9a7453f,
		FileId: 0x586e33e020cbf80f,
		TocDataOffset: 0,
		TocDataSize: 12,
		StreamOffset: 4096,
		StreamSize: 8,
		GpuResourceOffset: 8192,
		GpuResourceSize: 16);

	private sealed class RecordingResolver : IGameDataPackageResolver
	{
		public List<string> RequestedNames { get; } = new();

		public ValueTask<GameDataPackageToc?> GetPackageTocAsync(string packageName, CancellationToken cancellationToken = default)
			=> ValueTask.FromResult<GameDataPackageToc?>(null);

		public ValueTask<byte[]?> GetPackageResourceAsync(string packageName, ulong resourceOffset, uint resourceSize, CancellationToken cancellationToken = default)
		{
			RequestedNames.Add(packageName);
			return ValueTask.FromResult<byte[]?>(new byte[(int)resourceSize]);
		}

		public ValueTask<IReadOnlyList<string>> GetPackageNamesAsync(CancellationToken cancellationToken = default)
			=> ValueTask.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
	}
}
