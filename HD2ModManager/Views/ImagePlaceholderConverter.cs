using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HD2ModManager.Views
{
    // 作用：按显示目标尺寸加载图片，避免列表图标解码完整原图。
    public class ImagePlaceholderConverter : IValueConverter
    {
        private static readonly ConcurrentDictionary<string, BitmapSource> ImageCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Lazy<BitmapSource?> PlaceholderImage = new(LoadPlaceholderImage);

        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var decodePixelWidth = ParseDecodePixelWidth(parameter);
            var started = HD2ModManager.Services.UiPerformanceDiagnostics.IsEnabled
                ? System.Diagnostics.Stopwatch.GetTimestamp()
                : 0;
            try
            {
                var s = value as string;
                if (!string.IsNullOrWhiteSpace(s))
                {
                    try
                    {
                        var cacheKey = BuildCacheKey(s, decodePixelWidth);
                        return ImageCache.GetOrAdd(cacheKey, _ => LoadImageWithoutLock(s, decodePixelWidth));
                    }
                    catch { }
                }
                return PlaceholderImage.Value;
            }
            finally
            {
                HD2ModManager.Services.UiPerformanceDiagnostics.RecordElapsed(
                    "Thumbnail.Decode",
                    started,
                    thresholdMilliseconds: 15);
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }

        private static int ParseDecodePixelWidth(object parameter)
            => parameter is not null && int.TryParse(parameter.ToString(), out var width) ? width : 0;

        // 缓存键里带 mtime，是为了"图标被换掉"时能自然失效。但 mtime 的代价是
        // **每次** Convert 两次文件系统调用（File.Exists + GetLastWriteTimeUtc），
        // 而 Convert 跑在 UI 线程上，且列表每次 Reset 都会把可见行重新绑一遍
        // （见 docs/armor-registry-page-perf-20261027.md §5 E2）。
        //
        // 于是把 mtime 探针本身缓存一个很短的窗口：同一批重建里的重复绑定不再摸盘，
        // 而图标真的被替换时最多 2 秒就能看到新图。图标不是需要毫秒级新鲜度的数据。
        private const long MtimeProbeTtlTicks = TimeSpan.TicksPerSecond * 2;
        private static readonly ConcurrentDictionary<string, MtimeProbe> MtimeProbes = new(StringComparer.OrdinalIgnoreCase);

        private readonly record struct MtimeProbe(long Ticks, long SampledAtTicks);

        private static string BuildCacheKey(string path, int decodePixelWidth)
        {
            var writeTicks = SampleWriteTicks(path);
            return $"{path}\u001f{decodePixelWidth}\u001f{writeTicks}";
        }

        private static long SampleWriteTicks(string path)
        {
            var now = DateTime.UtcNow.Ticks;
            if (MtimeProbes.TryGetValue(path, out var probe) && now - probe.SampledAtTicks < MtimeProbeTtlTicks)
                return probe.Ticks;

            long ticks = 0;
            try
            {
                if (File.Exists(path)) ticks = File.GetLastWriteTimeUtc(path).Ticks;
            }
            catch { }
            MtimeProbes[path] = new MtimeProbe(ticks, now);
            return ticks;
        }

        private static BitmapSource? LoadPlaceholderImage()
        {
            try
            {
                var image = new BitmapImage(new Uri("pack://application:,,,/HD2ModManager;component/Resources/helldivers2.png", UriKind.Absolute));
                image.Freeze();
                return image;
            }
            catch
            {
                return null;
            }
        }

        private static BitmapImage LoadImageWithoutLock(string path, int decodePixelWidth)
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            if (decodePixelWidth > 0) bitmap.DecodePixelWidth = decodePixelWidth;
            if (File.Exists(path))
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                bitmap.StreamSource = stream;
                bitmap.EndInit();
            }
            else
            {
                bitmap.UriSource = new Uri(path, UriKind.RelativeOrAbsolute);
                bitmap.EndInit();
            }

            bitmap.Freeze();
            return bitmap;
        }
    }
}
