using System.Buffers.Binary;
using HD2ModAdaptation.Analysis;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using AdaptationAssetKey = HD2ModAdaptation.PatchReconstruction.AssetKey;

namespace HD2ModCore.Tests;

/// <summary>
/// 内容事实必须看见 <c>vpk2patch/</c> 里的 patch，并且以 <c>Overwrite/</c> 的变体为准。
/// <para>
/// 为什么单列：详情表格读的就是内容事实。之前它只枚举节点顶层，于是 VPK 版 Mod 的
/// patch 明明在库里、部署也能用，详情页却显示"没有 patch"——两处各写一遍枚举规则的代价。
/// </para>
/// </summary>
public sealed class ModContentFactsPayloadTests
{
	private const string PatchName = "9ba626afa44a3aa3.patch_0";

	[Fact]
	public async Task GetNodeFactsAsync_SeesPatchesInsideVpk2Patch()
	{
		var root = CreateTempRoot();
		try
		{
			var node = CreateNode("lunalice_bill");
			var directory = Path.Combine(root, node.RelativePath);
			Directory.CreateDirectory(Path.Combine(directory, ModNodePayloadLayout.VpkPatchDirectoryName));
			var patch = Path.Combine(directory, ModNodePayloadLayout.VpkPatchDirectoryName, PatchName);
			await File.WriteAllBytesAsync(patch, [1]);
			var provider = new CountingProvider(new Dictionary<string, IReadOnlyList<AdaptationAssetKey>>(StringComparer.OrdinalIgnoreCase)
			{
				[patch] = [new AdaptationAssetKey(10, 20)],
			});
			var service = new ModContentFactsService(new PatchFileNameParser(), provider);

			var facts = await service.GetNodeFactsAsync(node, root);

			var group = Assert.Single(facts.PatchGroups);
			Assert.Equal(new ModPatchGroupId(node.Id, "9ba626afa44a3aa3", 0), group.Id);
			Assert.Contains(new HD2ModCore.Domain.AssetKey(10, 20), group.AssetKeys);
		}
		finally
		{
			DeleteQuietly(root);
		}
	}

	[Fact]
	public async Task GetNodeFactsAsync_PrefersTheOverwriteVariantOfTheSameName()
	{
		var root = CreateTempRoot();
		try
		{
			var node = CreateNode("lunalice_bill");
			var directory = Path.Combine(root, node.RelativePath);
			var payloadDirectory = Path.Combine(directory, ModNodePayloadLayout.VpkPatchDirectoryName);
			var overwriteDirectory = Path.Combine(directory, ModNodePayloadLayout.OverwriteDirectoryName);
			Directory.CreateDirectory(payloadDirectory);
			Directory.CreateDirectory(overwriteDirectory);
			var payloadPatch = Path.Combine(payloadDirectory, PatchName);
			var overwritePatch = Path.Combine(overwriteDirectory, PatchName);
			await File.WriteAllBytesAsync(payloadPatch, [1]);
			// Overwrite 里的那份必须是**能读的 TOC**，否则不参与顶替。
			await File.WriteAllBytesAsync(overwritePatch, PatchToc());
			var provider = new CountingProvider(new Dictionary<string, IReadOnlyList<AdaptationAssetKey>>(StringComparer.OrdinalIgnoreCase)
			{
				[payloadPatch] = [new AdaptationAssetKey(10, 20)],
				[overwritePatch] = [new AdaptationAssetKey(10, 30)],
			});
			var service = new ModContentFactsService(new PatchFileNameParser(), provider);

			var facts = await service.GetNodeFactsAsync(node, root);

			var group = Assert.Single(facts.PatchGroups);
			Assert.Contains(new HD2ModCore.Domain.AssetKey(10, 30), group.AssetKeys);
			Assert.DoesNotContain(new HD2ModCore.Domain.AssetKey(10, 20), group.AssetKeys);
		}
		finally
		{
			DeleteQuietly(root);
		}
	}

	[Fact]
	public async Task GetNodeFactsAsync_IgnoresAnOverwriteFileWithoutAPayloadCounterpart()
	{
		var root = CreateTempRoot();
		try
		{
			var node = CreateNode("lunalice_bill");
			var directory = Path.Combine(root, node.RelativePath);
			var overwriteDirectory = Path.Combine(directory, ModNodePayloadLayout.OverwriteDirectoryName);
			Directory.CreateDirectory(overwriteDirectory);
			var stray = Path.Combine(overwriteDirectory, "0123456789abcdef.patch_3");
			await File.WriteAllBytesAsync(stray, PatchToc());
			var provider = new CountingProvider(new Dictionary<string, IReadOnlyList<AdaptationAssetKey>>(StringComparer.OrdinalIgnoreCase)
			{
				[stray] = [new AdaptationAssetKey(10, 40)],
			});
			var service = new ModContentFactsService(new PatchFileNameParser(), provider);

			var facts = await service.GetNodeFactsAsync(node, root);

			// Overwrite 是替换层：没有同名载荷就不是一次额外部署。
			Assert.Empty(facts.PatchGroups);
		}
		finally
		{
			DeleteQuietly(root);
		}
	}

	private static byte[] PatchToc()
	{
		var bytes = new byte[64];
		BinaryPrimitives.WriteUInt32LittleEndian(bytes, 4026531857);
		return bytes;
	}

	private static ModNode CreateNode(string relativePath)
		=> new(ModNodeId.New(), relativePath,
			new ModNodeMetadata(relativePath, null, DateTimeOffset.UtcNow, null), [], []);

	private static string CreateTempRoot()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		return root;
	}

	private static void DeleteQuietly(string path)
	{
		try { Directory.Delete(path, recursive: true); } catch (IOException) { }
	}

	/// <summary>只回答"这个 patch 里有哪些 AssetKey"，不读真实字节。</summary>
	private sealed class CountingProvider(IReadOnlyDictionary<string, IReadOnlyList<AdaptationAssetKey>> assets)
		: IPatchGroupAnalysisProvider
	{
		public ValueTask<IReadOnlyList<PatchGroupAnalysis>> AnalyzeNodeAsync(
			ModNode node,
			string modsRootDirectory,
			CancellationToken cancellationToken = default)
		{
			// 只认**实际存在**的文件：内容事实先按目录枚举，再交给分析器。
			var analyses = assets
				.Where(pair => File.Exists(pair.Key))
				.Select(pair => new PatchGroupAnalysis(
					new PatchGroupInput(pair.Key,
						File.Exists(pair.Key + ".stream") ? pair.Key + ".stream" : null,
						File.Exists(pair.Key + ".gpu_resources") ? pair.Key + ".gpu_resources" : null),
					pair.Value.Select(key => new PatchAssetFact(key, pair.Key, 0, 0, 0, false, false, false, false)).ToList(),
					Array.Empty<PatchAssetReference>(),
					Array.Empty<PatchAnalysisIssue>(),
					DateTimeOffset.UtcNow,
					"test"))
				.ToArray();
			return ValueTask.FromResult<IReadOnlyList<PatchGroupAnalysis>>(analyses);
		}
	}
}
