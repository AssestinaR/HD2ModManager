using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure.KitInstance;
using HD2ModManager.Services;

namespace HD2ModManager.ViewModels;

// 作用：详情页「共用部件」卡片（按实例隐藏 P2：发现污染）。
// 数据全部来自 KitPollutionService（索引库 + 材质缓存）；这里只负责把结果翻译成四种界面状态：
//   - 分析中                          ⇒ 卡片显示「正在分析……」
//   - 没有污染（受影响的套 < 2）      ⇒ 卡片隐藏（大多数 Mod 都是这样，不值得占一张卡的位置）
//   - 污染                            ⇒ 「这个 Mod 会影响 N 套护甲」+ 原因 + 举例，可展开看每个原因影响了哪些护甲
//   - 材质缓存没好                    ⇒ 追加「材质影响分析中」，先只给 Unit 部分的结论；缓存好了自动刷新
// 「转专属 / 撤销专属」与专属多选已撤（规划 kit-instance-mapping-deploy-plan §15.3）：想防污染去护甲映射页；这张卡只告知影响范围。
public partial class ModDetailsPageViewModel
{
    private const int KitPollutionHeadlineKits = 6;
    private const int KitPollutionDetailKits = 5;

    private KitPollutionService? _kitPollutionService;
    private int _kitPollutionVersion;

    public bool ShowKitPollutionCard { get; private set; }
    public string KitPollutionHeadline { get; private set; } = string.Empty;
    public string KitPollutionNote { get; private set; } = string.Empty;
    public bool HasKitPollutionNote => KitPollutionNote.Length > 0;
    public IReadOnlyList<string> KitPollutionDetails { get; private set; } = Array.Empty<string>();
    public bool HasKitPollutionDetails => KitPollutionDetails.Count > 0;

    private void SetKitPollution(bool show, string headline, string note, IReadOnlyList<string> details)
    {
        ShowKitPollutionCard = show;
        KitPollutionHeadline = headline;
        KitPollutionNote = note;
        KitPollutionDetails = details;
        OnPropertyChanged(nameof(ShowKitPollutionCard));
        OnPropertyChanged(nameof(KitPollutionHeadline));
        OnPropertyChanged(nameof(KitPollutionNote));
        OnPropertyChanged(nameof(HasKitPollutionNote));
        OnPropertyChanged(nameof(KitPollutionDetails));
        OnPropertyChanged(nameof(HasKitPollutionDetails));
    }

    // 和 RefreshDataIndexSummaryAsync 同一个刷新时机（它是被 RefreshAssetStatus 调起来的）。
    private async Task RefreshKitPollutionAsync(ModNodeId nodeId, ModContentFacts? facts)
    {
        var version = ++_kitPollutionVersion;

        if (facts is null)
        {
            RunOnUiThread(() => { if (!_disposed && version == _kitPollutionVersion) SetKitPollution(false, string.Empty, string.Empty, Array.Empty<string>()); });
            return;
        }

        try
        {
            var service = _kitPollutionService ??= new KitPollutionService(_paths);
            var assets = facts.Structure.Assets.Select(asset => asset.AssetKey).Distinct().ToArray();
            var gameData = SettingsService.GetGameDataFolder();
            // 没有 Unit / 材质 / 贴图的 Mod（Lua、音频等）不可能污染护甲：不闪「分析中」，直接隐藏。
            if (!assets.Any(key => key.TypeId is KitPollutionAnalyzer.UnitTypeId or KitPollutionAnalyzer.MaterialTypeId or KitPollutionAnalyzer.TextureTypeId))
            {
                RunOnUiThread(() => { if (!_disposed && version == _kitPollutionVersion) SetKitPollution(false, string.Empty, string.Empty, Array.Empty<string>()); });
                return;
            }
            RunOnUiThread(() =>
            {
                if (!_disposed && version == _kitPollutionVersion)
                    SetKitPollution(true, "正在分析这个 Mod 会影响哪些护甲……", string.Empty, Array.Empty<string>());
            });

            var result = await service.AnalyzeAsync(assets, gameData).ConfigureAwait(false);
            if (result is { MaterialAnalysisAvailable: false })
            {
                // 先把 Unit 部分的结论显示出来，再等后台构建完成后重算一次（不阻塞，也不轮询）。
                Present(result, materialPending: service.PendingMaterialBuild is not null);
                var pending = service.PendingMaterialBuild;
                if (pending is null) return;
                try { await pending.ConfigureAwait(false); }
                catch (Exception) { /* 构建失败由服务记录；下面重算时材质仍不可用，文案会如实说明 */ }
                if (version != _kitPollutionVersion) return;
                result = await service.AnalyzeAsync(assets, gameData).ConfigureAwait(false);
                Present(result, materialPending: false);
                return;
            }

            Present(result, materialPending: false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            RunOnUiThread(() =>
            {
                if (!_disposed && version == _kitPollutionVersion)
                    SetKitPollution(false, string.Empty, string.Empty, Array.Empty<string>());
            });
            _ = exception;
        }

        void Present(KitPollutionResult? value, bool materialPending)
        {
            RunOnUiThread(() =>
            {
                if (_disposed || version != _kitPollutionVersion) return;
                if (value is not null && !value.IsPolluted && materialPending)
                {
                    // Unit 部分没有结论、材质还在算：保持「分析中」，别让卡片消失又出现。
                    SetKitPollution(true, "正在分析这个 Mod 会影响哪些护甲（材质影响分析中）……", string.Empty, Array.Empty<string>());
                    return;
                }
                if (value is null || !value.IsPolluted)
                {
                    SetKitPollution(false, string.Empty, string.Empty, Array.Empty<string>());
                    return;
                }
                SetKitPollution(true, BuildKitPollutionHeadline(value), BuildKitPollutionNote(value, materialPending), BuildKitPollutionDetails(value));
            });
        }
    }

    internal static string BuildKitPollutionHeadline(KitPollutionResult result)
        => $"这个 Mod 会影响 {result.Kits.Count} 套护甲";

    // 先说原因（替换了什么、最大的那个原因一个就波及几套），再举几个看得懂的名字，最后是附加说明。
    internal static string BuildKitPollutionNote(KitPollutionResult result, bool materialPending)
    {
        var lines = new List<string>();
        var replaced = KitPollutionAnalyzer.DescribeReplaced(result);
        if (replaced.Length > 0)
        {
            var reason = $"原因：它替换了 {replaced}，这些资源原版护甲也在用，替换后用到它们的护甲会一起变样。";
            var top = result.CauseList.FirstOrDefault();
            if (top is not null && result.CauseList.Count > 1)
                reason += $"其中{top.KindLabel} {top.ShortId} 一个就影响 {top.Kits.Count} 套。";
            lines.Add(reason);
        }

        var names = KitPollutionAnalyzer.PickLabels(result.Kits, KitPollutionHeadlineKits, out var complete);
        if (names.Count > 0)
            lines.Add($"包括 {string.Join("、", names)}{(complete ? string.Empty : $" 等 {result.Kits.Count} 套")}。");

        if (!result.MaterialAnalysisAvailable)
            lines.Add(materialPending ? "材质影响分析中，目前只列出了部件部分的结论。" : "材质影响暂时无法分析，目前只列出了部件部分的结论。");
        if (result.VanillaTextureCount > 0)
            lines.Add($"另有 {result.VanillaTextureCount} 张原版贴图被全局替换，影响范围未分析。");
        return string.Join(Environment.NewLine, lines);
    }

    // 按原因分组，一个原因一行：几百行的「每套一行」没法看，而它们的原因通常就那几个资源。
    internal static IReadOnlyList<string> BuildKitPollutionDetails(KitPollutionResult result)
        => result.CauseList.Select(cause =>
        {
            var names = KitPollutionAnalyzer.PickLabels(cause.Kits, KitPollutionDetailKits, out var complete);
            var tail = complete ? string.Empty : " 等";
            return $"{cause.KindLabel} {cause.ShortId}：{cause.Kits.Count} 套（{string.Join("、", names)}{tail}）";
        }).ToArray();
}
