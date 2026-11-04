namespace HD2ModManager.Services;

// 装饰包已不再支持：`DecorationActivationSummary` 已删除。这个文件现在只剩选项那一份。
public sealed record OptionActivationSummary(int EnabledHostCount, int AvailableHostCount, bool IsEnabledForAllAvailableHosts, string StatusText);
