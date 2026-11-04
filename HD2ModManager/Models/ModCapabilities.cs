namespace HD2ModManager.Models;

// View and command policy derived from the persistent Mod kind.
// 装饰包已不再支持：`SupportsDecorationPlan` / `SupportsDecorationAttachments` 两个标志
// 已删除（零消费者），`isDecoration` 那条分支也删掉了（库里不可能再有装饰节点）。
// `isDecoration` 参数保留，因为 `ModEntity.Capabilities` 还在传它；它现在恒为 false。
public sealed record ModCapabilities(
    bool CanJoinProfile,
    bool CanDeploy,
    bool ShowsPatchAssets,
    bool ShowsCompatibility,
    bool SupportsPatchTools)
{
    public static ModCapabilities For(bool isDecoration, bool isOption = false, bool hasPatch = true, bool isReShade = false)
        => isReShade
            ? new(true, true, false, false, false)
            : isOption
                ? new(false, false, hasPatch, hasPatch, hasPatch)
                : hasPatch
                    ? new(true, true, true, true, true)
                    // An empty host remains selectable so its attached options
                    // can participate in deployment, but exposes no patch tools.
                    : new(true, false, false, false, false);
}
