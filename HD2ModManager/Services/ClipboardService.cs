using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace HD2ModManager.Services
{
    /// <summary>
    /// 安全写入系统剪贴板。Windows 剪贴板为全局互斥资源，被其他进程短暂占用时
    /// WPF Clipboard.SetText 会直接抛出 CLIPBRD_E_CANT_OPEN (0x800401D0)，
    /// 因此统一在此做短时重试与异常兜底，避免触发未处理 UI 异常。
    /// </summary>
    internal static class ClipboardService
    {
        private const int MaxAttempts = 5;
        private const int RetryDelayMs = 20;

        public static bool TrySetText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    System.Windows.Clipboard.SetText(text);
                    return true;
                }
                catch (ExternalException) when (attempt < MaxAttempts)
                {
                    Thread.Sleep(RetryDelayMs);
                }
                catch (ExternalException ex)
                {
                    LogService.Error($"写入剪贴板失败（0x{ex.ErrorCode:X8}）：{ex.Message}");
                    return false;
                }
                catch (Exception ex)
                {
                    LogService.Error($"写入剪贴板失败：{ex.Message}");
                    return false;
                }
            }

            return false;
        }
    }
}
