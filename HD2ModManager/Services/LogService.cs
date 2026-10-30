using System;
using System.Globalization;
using System.IO;
using System.Threading;

namespace HD2ModManager.Services
{
    /// <summary>
    /// 两条通道，按"谁在读"分开：
    ///
    ///   manager_yyyyMMdd.log —— 用户级。启动、导入、部署、失败，以及值得回看的异常路径。
    ///                           用户报问题时发出来的就是这一份，它必须保持可读。
    ///   trace_yyyyMMdd.log   —— 开发级。性能采集、信息中心流水这类内部记账。
    ///                           采集代码可以常驻，但它不往用户日志里写字。
    ///
    /// 分通道而不是加日志级别：级别只能决定"写不写"，决定不了"写到哪"；
    /// 而这里真正的问题是两类读者的信息混在同一个文件里。
    ///
    /// 两份日志都按天切分，并只保留 <see cref="RetentionDays"/> 天。
    /// </summary>
    public static class LogService
    {
        private const int RetentionDays = 14;

        private static readonly object _lock = new object();
        private static int _pruned;

        private static string LogDirectory => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
        private static string ManagerPath => Path.Combine(LogDirectory, $"manager_{DateTime.Now:yyyyMMdd}.log");
        private static string TracePath => Path.Combine(LogDirectory, $"trace_{DateTime.Now:yyyyMMdd}.log");

        public static void Info(string message)
        {
            Write(ManagerPath, "INFO", message);
        }
        public static void Error(string message)
        {
            Write(ManagerPath, "ERROR", message);
        }
        public static void Warn(string message)
        {
            Write(ManagerPath, "WARN", message);
        }

        /// <summary>
        /// 开发级流水，写 trace 通道。category 作为行首标签（如 UIPerf、信息中心），
        /// 这样一份 trace 里能直接筛出某个子系统。
        /// </summary>
        public static void Trace(string category, string message)
        {
            Write(TracePath, "TRACE", $"{category} {message}");
        }

        private static void Write(string path, string level, string message)
        {
            try
            {
                var dir = Path.GetDirectoryName(path)!;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                PruneOnce(dir);
                var line = $"[{DateTimeOffset.Now:O}] {level} {message}";
                lock (_lock)
                {
                    File.AppendAllText(path, line + Environment.NewLine);
                }
            }
            catch { }
        }

        /// <summary>
        /// 每天的文件只增不减，攒两个月就是十几兆。每个进程启动后第一次写日志时清一次，
        /// 只删自己这两类文件（按文件名里的日期判断），别人的日志不碰。
        /// </summary>
        private static void PruneOnce(string directory)
        {
            if (Interlocked.Exchange(ref _pruned, 1) != 0) return;
            try
            {
                var cutoff = DateTime.Now.Date.AddDays(-RetentionDays);
                foreach (var file in Directory.EnumerateFiles(directory, "*.log"))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (name.Length <= 8) continue;
                    if (!DateTime.TryParseExact(name[^8..], "yyyyMMdd", CultureInfo.InvariantCulture,
                            DateTimeStyles.None, out var date)) continue;
                    if (date >= cutoff) continue;
                    File.Delete(file);
                }
            }
            catch { }
        }
    }
}
