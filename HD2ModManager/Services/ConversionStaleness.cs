using System;
using System.IO;
using HD2ModCore.Infrastructure;

namespace HD2ModManager.Services
{
    /// <summary>
    /// 判断 Mod 目录里的 VPK 转换产物是不是<b>当前这份转换器</b>做的。
    /// <para>
    /// <b>为什么需要它。</b>2026-11-03 实测踩过：改完转换器行为、也点了「重构建」，
    /// 但 MourningVeil 的 <c>B3服装色白紫</c> 那一包没被刷到（其余 11 包都刷了），
    /// 于是连续三轮进游戏测试都在看 11-02 的旧产物，还在怀疑代码没生效。
    /// </para>
    /// <para>
    /// 当时唯一的线索是文件 mtime，而 mtime 分不清"重构建过但内容没变"和"根本没重构建"。
    /// 现在转换报告里带 <c>conversion.toolStamp</c>（生成它的 hd2mod.exe 构建时间），
    /// 与当前 hd2mod.exe 的时间戳一比就清楚。
    /// </para>
    /// </summary>
    public static class ConversionStaleness
    {
        /// <summary>
        /// 当前会用来跑转换的那个 <c>hd2mod.exe</c> 的时间戳（UTC ISO-8601）。
        /// 拿不到返回 <c>null</c> —— 那时一律按"可能过期"处理。
        /// </summary>
        public static string? CurrentToolStamp()
        {
            try
            {
                var path = Hd2ModCliLocator.TryResolve();
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    return null;
                return File.GetLastWriteTimeUtc(path).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return null;
            }
        }

        /// <summary>
        /// 这个 Mod 目录的产物是否由当前转换器生成。
        /// <para>没有转换报告（不是 VPK 版 Mod）时返回 <c>false</c>，调用方据此跳过。</para>
        /// </summary>
        public static bool IsFromCurrentTool(string modDirectory, string? currentStamp = null)
        {
            var report = VpkConversionReport.TryRead(modDirectory);
            return report is not null && report.IsFromBuild(currentStamp ?? CurrentToolStamp());
        }
    }
}
