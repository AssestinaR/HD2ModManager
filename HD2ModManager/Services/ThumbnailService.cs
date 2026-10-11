using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using HD2ModCore.Application;
using HD2ModCore.Infrastructure;

namespace HD2ModManager.Services
{
    // 作用：仅根据 ThumbnailSourceFacts 渲染并提供小尺寸图片缓存；源事实由信息中心负责。
    // Purpose: Renders and serves the small image cache from ThumbnailSourceFacts only; the source facts belong to the information center.
    //
    // 阶段 5（设计文档 §27）：本服务已降级为**纯渲染器**——只解码 / 落盘，不再自己判新鲜。
    // 磁盘键 = 源图像的"内容指纹"（ThumbnailFingerprint.OfSource），与信息中心判事实新鲜度
    // 用的是同一个定义，于是全库只有一套缩略图失效协议：
    //   源图像换了内容 ⇒ 指纹变 ⇒ 键变 ⇒ 旧文件自然失效，无需任何外部事件来删缓存。
    public static class ThumbnailService
    {
        private static readonly string CacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache", "thumbs");
        private static readonly object SyncRoot = new();

        // 历史上生成过的缩略图尺寸。按源删除与磁盘清扫都必须覆盖全部尺寸，
        // 否则会出现"删了却还在"的漏删（只占磁盘，不影响正确性）。
        public static IReadOnlyList<int> KnownSizes { get; } = new[] { 72, 128, 256 };

        public static string? GetExistingThumbnailPath(string? originalPath, int decode)
        {
            var started = UiPerformanceDiagnostics.IsEnabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            string? result = null;
            // OfSource 内含一次 stat，文件不存在时返回 Missing——不必再单独 File.Exists。
            var fingerprint = ThumbnailFingerprint.OfSource(originalPath);
            if (fingerprint != ThumbnailFingerprint.Missing)
            {
                var thumbnail = ComposeThumbPath(fingerprint, decode);
                result = File.Exists(thumbnail) ? thumbnail : null;
            }

            UiPerformanceDiagnostics.RecordElapsed("Thumbnail.ExistingPath", started, thresholdMilliseconds: 8);
            return result;
        }

        public static string? GetExistingThumbnailPath(ModThumbnailFacts? facts, int decode)
            => facts?.SourcePath is { } source ? GetExistingThumbnailPath(source, decode) : null;

        public static Task<bool> EnsureThumbnailAsync(ModThumbnailFacts facts, int decode, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(facts);
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fingerprint = ThumbnailFingerprint.OfSource(facts.SourcePath);
                if (fingerprint == ThumbnailFingerprint.Missing) return false;
                // 只算一次键：既是"要不要渲染"的判据，也是渲染的目标名。
                var thumbnail = ComposeThumbPath(fingerprint, decode);
                if (File.Exists(thumbnail)) return false;
                EnsureThumb(facts.SourcePath!, thumbnail, decode);
                return true;
            }, cancellationToken);
        }

        // 作用：把 cache/thumbs 收敛成"当前库 × 当前内容指纹"应有的那一组文件（设计文档 §27.5）。
        //  ① 旧键迁移：源图未变时，把旧版"只有路径哈希"的文件改名到新键，
        //     于是升级首启不必重生成整库缩略图（改名是 File.Move，不是重新解码）。
        //  ② 无主清扫：其余不属于任何当前图标的文件删除（源已删除 / 源已替换 / 崩溃残留）。
        // 这是垃圾回收，不是第二套失效协议：删错或漏删都不会让"新鲜度"判断出错，只影响磁盘占用。
        // 幂等且与顺序无关——即使库页刷新抢先把新键文件生成出来，这里也只是把旧键当孤儿删掉。
        public static Task<ThumbnailSweepResult> SweepAsync(
            IEnumerable<string?> currentSourcePaths,
            IReadOnlyList<int> sizes,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(currentSourcePaths);
            ArgumentNullException.ThrowIfNull(sizes);
            var sources = currentSourcePaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var sizeList = sizes.Where(size => size > 0).Distinct().ToArray();

            return Task.Run(() =>
            {
                if (sizeList.Length == 0) return new ThumbnailSweepResult(0, 0);
                var startedUtc = DateTime.UtcNow;
                Directory.CreateDirectory(CacheDir);
                var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var migrated = 0;
                foreach (var source in sources)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var fingerprint = ThumbnailFingerprint.OfSource(source);
                    if (fingerprint == ThumbnailFingerprint.Missing) continue;
                    var sourceWrittenUtc = File.GetLastWriteTimeUtc(source);
                    foreach (var size in sizeList)
                    {
                        var target = ComposeThumbPath(fingerprint, size);
                        expected.Add(Path.GetFileName(target));
                        if (File.Exists(target)) continue;
                        var legacy = ComposeThumbPath(LegacyDiskKey(source), size);
                        if (!File.Exists(legacy)) continue;
                        // 旧缩略图不比源图旧 ⇒ 它代表的正是当前内容 ⇒ 改名复用，不重新解码。
                        // 源图比缩略图新（换过图但没删缓存）时跳过，落到下面的清扫当孤儿删掉。
                        if (File.GetLastWriteTimeUtc(legacy) < sourceWrittenUtc) continue;
                        try { File.Move(legacy, target); migrated++; } catch { }
                    }
                }

                var removed = 0;
                foreach (var file in Directory.EnumerateFiles(CacheDir, "*.png", SearchOption.TopDirectoryOnly))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (expected.Contains(Path.GetFileName(file))) continue;
                    // 只清理早于本次开始的文件：不与并发进行的缩略图写入抢文件。
                    if (File.GetLastWriteTimeUtc(file) >= startedUtc) continue;
                    try { File.Delete(file); removed++; } catch { }
                }

                return new ThumbnailSweepResult(migrated, removed);
            }, cancellationToken);
        }

        private static void EnsureThumb(string originalPath, string thumbPath, int decode)
        {
            lock (SyncRoot)
            {
                if (File.Exists(thumbPath)) return;
                Directory.CreateDirectory(CacheDir);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                using (var input = new FileStream(originalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    bmp.StreamSource = input;
                    if (decode > 0) bmp.DecodePixelWidth = decode;
                    bmp.EndInit();
                }

                bmp.Freeze();
                using var fs = new FileStream(thumbPath, FileMode.Create, FileAccess.Write, FileShare.None);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bmp));
                encoder.Save(fs);
            }
        }

        private static string ComposeThumbPath(string fingerprint, int decode)
        {
            var sizeTag = decode > 0 ? $"_{decode}" : string.Empty;
            return Path.Combine(CacheDir, fingerprint + sizeTag + ".png");
        }

        // 旧版磁盘键：只有源路径的哈希（阶段 5 之前）。只被一次性迁移用到。
        private static string LegacyDiskKey(string originalPath)
        {
            string absolute;
            try { absolute = Path.GetFullPath(originalPath); } catch { absolute = originalPath; }
            using var sha = System.Security.Cryptography.SHA256.Create();
            var bytes = System.Text.Encoding.UTF8.GetBytes(absolute);
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
        }
    }

    // 作用：一次性清扫的结果，仅用于日志（设计文档 §27.5）。
    public readonly record struct ThumbnailSweepResult(int Migrated, int Removed);
}
