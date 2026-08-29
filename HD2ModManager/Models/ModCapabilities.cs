namespace HD2ModManager.Models;

// View and command policy derived from the persistent Mod kind.
public sealed record ModCapabilities(
    bool CanJoinProfile,
    bool CanDeploy,
    bool ShowsPatchAssets,
    bool ShowsCompatibility,
    bool SupportsPatchTools,
    bool SupportsDecorationPlan,
    bool SupportsDecorationAttachments)
{
    public static ModCapabilities For(bool isDecoration, bool isOption = false, bool hasPatch = true, bool isReShade = false)
        => isReShade
            ? new(true, true, false, false, false, false, false)
            : isDecoration
            ? new(false, false, false, false, false, true, false)
            : isOption
                ? new(false, false, hasPatch, hasPatch, hasPatch, hasPatch, false)
                : hasPatch
                    ? new(true, true, true, true, true, false, true)
                    // An empty host remains selectable so its attached options
                    // can participate in deployment, but exposes no patch tools.
                    : new(true, false, false, false, false, false, false);
}
