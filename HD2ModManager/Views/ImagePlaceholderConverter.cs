using System;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace HD2ModManager.Views
{
    // 作用：按显示目标尺寸加载图片，避免列表图标解码完整原图。
    public class ImagePlaceholderConverter : IValueConverter
    {
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
                    try { return LoadImageWithoutLock(s, decodePixelWidth); } catch { }
                }
                // fallback to embedded resource
                try
                {
                    var uri = new Uri("pack://application:,,,/HD2ModManager;component/Resources/helldivers2.png", UriKind.Absolute);
                    return new BitmapImage(uri);
                }
                catch
                {
                    return null;
                }
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
