using HD2ModCore.Application;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

// 作用：从 Mod 目录约定图像生成轻量 Thumbnail 信息，不做图像解码或 Patch 分析。
// Purpose: Produces lightweight thumbnail facts from convention-based Mod images without decoding or Patch analysis.
//
// generation 不再自己算：与中心用同一个定义（ThumbnailFingerprint.OfNode），
// 否则"同一个节点的缩略图 generation"会有两个不同的字符串（设计文档 §27.2）。
public sealed class ModThumbnailProducer : IModThumbnailProducer
{
	public ValueTask<ModThumbnailFacts> ProduceAsync(ModNode node, string modsRootDirectory, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(node);
		cancellationToken.ThrowIfCancellationRequested();
		var directory = Path.Combine(modsRootDirectory, node.RelativePath);
		var source = ModIconLocator.TryResolve(directory);
		FileInfo? info = source is null ? null : new FileInfo(source);
		var generation = ThumbnailFingerprint.OfNode(node.Id, source);
		return ValueTask.FromResult(new ModThumbnailFacts(node.Id, node.RelativePath, generation, DateTimeOffset.UtcNow, source, info?.Length, info?.LastWriteTimeUtc, Array.Empty<CoreIssue>()));
	}
}
