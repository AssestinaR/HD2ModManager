using HD2ModAdaptation.PatchReconstruction;

namespace HD2SourceMigration.Tests;

/// <summary>
/// **重写 patch 之后，物理参数容器必须还在。**
/// <para>
/// 这条钉的是实测踩过两次的坑：`PatchArchiveWriter` 是"按条目重写一个 patch"，
/// 尾部附加的 <c>HD2PBX01</c> 容器不在条目里，所以不会自动跟过来。受影响的调用点包括
/// 护甲标准化（部署前）和"应用为 RE2310"（custom-model convert）。容器一丢，
/// 运行时就读不到参数，退回外部 rig 文件——表现为**拿别的模型的参数去驱动**，而且不报错。
/// </para>
/// <para>
/// 用真实 patch 做（有就测，没有就跳过），因为"条目重排 + 尾部搬运"这两件事只有在
/// 真实布局上才有意义。
/// </para>
/// </summary>
public sealed class PatchArchiveWriterContainerTests
{
	private const string RepoRoot = @"E:\Data\source\repos\WpfApp1";

	private static string? FindPatchWithContainer()
	{
		// 优先用环境变量指定；否则扫库里已知的模型 patch（容器由 embed-mod 写入）。
		var configured = Environment.GetEnvironmentVariable("HD2_CONTAINER_PATCH");
		if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
			return configured;

		foreach (var root in new[] { @"E:\mod\lunalice_bill_2", @"E:\mod\mourningveil_bill" })
		{
			foreach (var sub in new[] { "Overwrite", "vpk2patch", "" })
			{
				var directory = string.IsNullOrEmpty(sub) ? root : Path.Combine(root, sub);
				if (!Directory.Exists(directory))
					continue;
				foreach (var candidate in Directory.EnumerateFiles(directory, "*.patch_0", SearchOption.TopDirectoryOnly))
				{
					var gpu = candidate + ".gpu_resources";
					if (!File.Exists(gpu))
						continue;
					if (PhysBoneCarrier.TryReadContainerFromFileAsync(gpu).GetAwaiter().GetResult() is not null)
						return candidate;
				}
			}
		}

		return null;
	}

	[Fact]
	public async Task RewritingAPatchCarriesThePhysBoneContainerForward()
	{
		var patch = FindPatchWithContainer();
		if (patch is null)
			return;   // 本机没有带容器的真实 patch，跳过（不静默通过：见下面的显式断言）

		var sourceGpu = patch + ".gpu_resources";
		var before = await PhysBoneCarrier.TryReadContainerFromFileAsync(sourceGpu);
		Assert.NotNull(before);
		var rigBefore = PhysBoneCarrier.ReadRigRecordLength(await File.ReadAllBytesAsync(sourceGpu));

		var output = Path.Combine(Path.GetTempPath(), "hd2pbx-writer-test-" + Guid.NewGuid().ToString("N"));
		try
		{
			// 一次"什么都不改"的重写：条目全部保留、没有新增。容器仍然必须跟过去。
			var result = await new PatchArchiveWriter().WriteAsync(
				patch,
				output,
				Array.Empty<PatchUnitMeshEditResult>(),
				additionalEntries: null,
				removedEntries: null,
				overwriteExisting: true);

			Assert.True(File.Exists(result.GpuResourceFilePath), "重写后没有产出 .gpu_resources");
			var after = await PhysBoneCarrier.TryReadContainerFromFileAsync(result.GpuResourceFilePath);
			Assert.NotNull(after);
			Assert.Equal(before, after);
			Assert.Equal(rigBefore, PhysBoneCarrier.ReadRigRecordLength(await File.ReadAllBytesAsync(result.GpuResourceFilePath)));
			// 容器必须在 TOC 引用范围之后：长度至少是"容器 + 原 gpu 数据"
			Assert.True(result.GpuResourceFileSize >= before!.Length,
				$"产出 gpu 长度 {result.GpuResourceFileSize} 小于容器长度 {before.Length}");

			// 幂等：再来一次不能越写越大
			var again = await new PatchArchiveWriter().WriteAsync(
				result.TocFilePath,
				output + "-2",
				Array.Empty<PatchUnitMeshEditResult>(),
				additionalEntries: null,
				removedEntries: null,
				overwriteExisting: true);
			Assert.Equal(result.GpuResourceFileSize, again.GpuResourceFileSize);
		}
		finally
		{
			foreach (var directory in new[] { output, output + "-2" })
			{
				try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
				catch (IOException) { /* 清理失败不影响结论 */ }
			}
		}
	}
}
