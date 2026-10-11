namespace HD2ModCore.Tests;

// 作用：钉住"缩略图只有一套失效协议"（设计文档 §27）。
// Purpose: Pins that the thumbnail pipeline has exactly one invalidation protocol (design doc §27).
//
// 阶段 5 之前，缩略图有两套互不相干的失效协议：
//   事实缓存按 generation 失效；磁盘缓存靠**事件驱动的手工删除**（两处调用点）失效。
// 后者不是规则，而是"没人记得删就不失效"——它已经造成一个真实 bug：
// 同名换图标后卡片永远显示旧图（§27.1）。
// 现在两边共用同一个"内容指纹"定义，磁盘键由源内容决定，于是事件驱动的删除整个消失。
// 这些断言把"不再回头"固定下来。
public sealed class ThumbnailPipelineArchitectureTests
{
	[Fact]
	public void ThumbnailGenerationHasExactlyOneDefinition()
	{
		var root = FindRepositoryRoot();
		var fingerprint = File.ReadAllText(Path.Combine(root, "HD2ModCore", "Infrastructure", "ThumbnailFingerprint.cs"));

		// 正锚点：唯一定义本身必须真的在算哈希，否则下面的"别处不许算"会变成空断言。
		Assert.Contains("SHA256", fingerprint);
		Assert.Contains("public static string OfSource(", fingerprint);
		Assert.Contains("public static string OfNode(", fingerprint);

		// 生产者：改成委托，且自己不再算哈希。
		var producer = File.ReadAllText(Path.Combine(root, "HD2ModCore", "Infrastructure", "ModThumbnailProducer.cs"));
		Assert.Contains("ThumbnailFingerprint.OfNode(", producer);
		Assert.DoesNotContain("SHA256", producer);

		// 中心：同样委托。曾经它和生产者算出的是**两个不同的字符串**（少一个分隔符、少一次小写化、
		// 无图标时退化成"整个 Mod 目录的哈希"），这正是"双路"的根源。
		var center = File.ReadAllText(Path.Combine(root, "HD2ModCore", "Infrastructure", "ModInformationCenter.cs"));
		var compute = Slice(center, "private static string ComputeThumbnailGeneration", "\n\tprivate ");
		Assert.True(compute.Length > 0, "无法定位 ComputeThumbnailGeneration。");
		Assert.Contains("ThumbnailFingerprint.OfNode(", compute);
		Assert.DoesNotContain("SHA256", compute);
		Assert.DoesNotContain("ComputeNodeGeneration", compute);
	}

	[Fact]
	public void ThumbnailDiskKeyIsTheSharedContentFingerprint()
	{
		var root = FindRepositoryRoot();
		var service = File.ReadAllText(Path.Combine(root, "HD2ModManager", "Services", "ThumbnailService.cs"));

		// 磁盘键 = 源图内容指纹。这是"ThumbnailService 降级为纯渲染器"的判据：
		// 它不再判断"要不要重算"，只是按源内容算出文件名然后渲染。
		Assert.Contains("ThumbnailFingerprint.OfSource(", service);
		Assert.Contains("ComposeThumbPath(", service);

		// 读路径不得再碰旧键：旧键只允许出现在一次性迁移里。
		var read = Slice(service,
			"public static string? GetExistingThumbnailPath(string? originalPath",
			"public static string? GetExistingThumbnailPath(ModThumbnailFacts");
		Assert.True(read.Length > 0, "无法定位 GetExistingThumbnailPath(string?, int)。");
		Assert.Contains("ThumbnailFingerprint.OfSource(", read);
		Assert.DoesNotContain("LegacyDiskKey", read);
		Assert.DoesNotContain("SHA256", read);

		// 迁移只允许出现在清扫里，且必须被明确标注为旧键。
		var sweep = Slice(service, "public static Task<ThumbnailSweepResult> SweepAsync(", "\n        private static void EnsureThumb(");
		Assert.True(sweep.Length > 0, "无法定位 SweepAsync。");
		Assert.Contains("LegacyDiskKey(", sweep);
		Assert.Contains("旧版磁盘键", service);
	}

	// 旧管道整个消失：三个零调用的公开方法，加上一个"取消一个没人读的令牌"的空操作。
	[Fact]
	public void TheLegacyThumbnailPipelineIsGone()
	{
		var root = FindRepositoryRoot();
		var service = File.ReadAllText(Path.Combine(root, "HD2ModManager", "Services", "ThumbnailService.cs"));

		foreach (var dead in new[] { "PreGenerateAll", "EnsureThumbnailsAsync", "RegenerateThumbnailAsync", "CancelPendingGeneration", "_generationCancellation" })
			Assert.DoesNotContain(dead, service);

		// 全生产代码里也不该再有它们的调用点。
		var production = EnumerateProductionSources(Path.Combine(root, "HD2ModManager"))
			.Concat(EnumerateProductionSources(Path.Combine(root, "HD2ModCore")))
			.ToArray();
		foreach (var dead in new[] { "PreGenerateAll", "CancelPendingGeneration", "DeleteCachedThumbnailsForSource" })
		{
			var violations = production
				.Where(path => File.ReadAllText(path).Contains(dead, StringComparison.Ordinal))
				.Select(path => Path.GetRelativePath(root, path))
				.ToArray();
			Assert.Empty(violations);
		}
	}

	// 失效协议只有一套，垃圾回收也只有一个入口：启动时的一次性清扫。
	[Fact]
	public void ThumbnailCacheGarbageIsCollectedInOnePlace()
	{
		var root = FindRepositoryRoot();

		var library = File.ReadAllText(Path.Combine(root, "HD2ModManager", "Services", "ModLibraryService.cs"));
		// 换内容 ⇒ 指纹变 ⇒ 键变，旧文件自然失效，所以"替换 Mod 文件"流程不再手工删缓存（§27.4）。
		Assert.Contains("public Task<ThumbnailSweepResult> SweepThumbnailCacheAsync(", library);

		var shell = File.ReadAllText(Path.Combine(root, "HD2ModManager", "ViewModels", "ShellViewModel.cs"));
		Assert.Contains("SweepThumbnailCacheAsync(", shell);
		Assert.Contains("private async Task SweepThumbnailCacheAsync(", shell);
		// 必须在库快照落定（启动维护完成）之后起跑，否则"当前库"这份输入是空的。
		var startupCheck = shell.IndexOf("await RunStartupChecksAsync(", StringComparison.Ordinal);
		var sweepCall = shell.IndexOf("Task.Run(() => SweepThumbnailCacheAsync(", StringComparison.Ordinal);
		Assert.True(startupCheck >= 0 && sweepCall > startupCheck, "缩略图清扫必须在启动维护之后起跑。");
	}

	private static string Slice(string source, string startMarker, string endMarker)
	{
		var start = source.IndexOf(startMarker, StringComparison.Ordinal);
		if (start < 0) return string.Empty;
		var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
		return end > start ? source[start..end] : source[start..];
	}

	private static string[] EnumerateProductionSources(string projectDirectory)
		=> Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories)
			.Where(path => !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
			.Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
			.ToArray();

	private static string FindRepositoryRoot()
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);
		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WpfApp1.sln")))
			directory = directory.Parent;
		return directory?.FullName ?? throw new DirectoryNotFoundException("Unable to locate the repository root.");
	}
}
