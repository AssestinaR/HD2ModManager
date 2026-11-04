using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows.Input;
using HD2ModManager.Controls.Cards;
using HD2ModManager.ViewModels;

namespace UiSnapshot;

// 只用于离屏渲染的替身：属性名与 ModDetailsPageView 的绑定路径一一对应。
// 真正的 ModDetailsPageViewModel 需要十几个服务，这里只提供"看起来像"的数据。
// 必须是 public：绑定发生在 HD2ModManager 程序集里，跨程序集访问 internal 会失败。
public sealed class ModDetailsStub
{
    public string ModId { get; set; } = "stub-mod-0001";
    public string Name { get; set; } = "【4.28】Chiffon-LUNALICE 替换 RE-2310";
    public string? Image { get; set; }
    public string Description { get; set; } = "替换 RE-2310 全套护甲；含头盔、披风与胸甲三件。";
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public bool IsDecoration { get; set; }
    public bool IsVpkMod { get; set; }
    public bool ShowPatchSettingsCard { get; set; } = true;
    public bool CanUsePatchTools { get; set; } = true;
    public bool CanShowDecorationPlan { get; set; } = true;
    public bool ShowUserStatusCard { get; set; }
    public bool ShowOutdatedCompatibility { get; set; }
    public bool IsModelOutdated { get; set; }

    public string UserStatusTitle { get; set; } = "已启用";
    public string UserStatusSummary { get; set; } = "当前活动配置已包含这个 Mod，游戏启动后会部署。";
    public string AssetTagsString { get; set; } =
        "armor 0x3f2a91c4d0b7e615\nhelmet 0x88a1c30f5b2d7e94\ncape 0x51de0c77a9f3b268";
    public string DataIndexSummary { get; set; } = "命中 12 个 Archive，Top 1：RE-2310“荣誉卫队”";
    public string UnitCompatibilitySummary { get; set; } = "模型来自 2025-11 版本，当前游戏已更新。";
    public string HelmetMergeToggleText { get; set; } = "关闭头盔合并";

    public bool ShowVpkComponentCard { get; set; }
    public bool HasVpkComponentStatus { get; set; }
    public string VpkComponentStatus { get; set; } = string.Empty;
    public bool EnableVpkComponentTree { get; set; }
    public IReadOnlyList<string> VpkTargetArmors { get; } = new[] { "RE-2310“荣誉卫队”", "CE-27 侦察兵", "FS-11 拆弹专家" };
    public object? SelectedVpkTargetArmor { get; set; }
    public VpkTreeStub VpkTree { get; } = new();

    public IReadOnlyList<object> HostDecorations { get; } = Array.Empty<object>();
    public bool HasHostDecorations => false;
    public bool CanReorderHostOptions { get; set; }

    // 与真实 VM 使用同一份卡片声明，保证 before/after 对比的是外观而不是数据。
    private ObservableCollection<CardDescriptor>? _cards;

    public ObservableCollection<CardDescriptor> Cards
    {
        get
        {
            if (_cards is not null) return _cards;

            _cards = new ObservableCollection<CardDescriptor>(
                ModDetailsCardLayout.Create(this, ApplyVpkComponentsCommand));
            ModDetailsCardLayout.Sync(_cards, new ModDetailsCardState(
                HasDescription: HasDescription,
                CanUsePatchTools: CanUsePatchTools,
                ShowPatchSettingsCard: ShowPatchSettingsCard,
                IsDecoration: IsDecoration,
                CanShowDecorationPlan: CanShowDecorationPlan,
                IsVpkMod: IsVpkMod,
                ShowVpkComponentCard: ShowVpkComponentCard,
                ShowUserStatusCard: ShowUserStatusCard,
                ShowOutdatedCompatibility: ShowOutdatedCompatibility));
            return _cards;
        }
    }

    public ICommand Noop { get; } = new AlwaysCommand();

    public ICommand ApplyVpkComponentsCommand => Noop;
    public ICommand ApplyVpkTargetArmorCommand => Noop;
    public ICommand ConvertToCustomModelCommand => Noop;
    public ICommand NormalizeArmorCommand => Noop;
    public ICommand OpenAdvancedDetailsCommand => Noop;
    public ICommand OpenFolderCommand => Noop;
    public ICommand PatchResourceRepackagingCommand => Noop;
    public ICommand PlanBatchDecorationCommand => Noop;
    public ICommand PlanCrossArmorTransferCommand => Noop;
    public ICommand PlanDecorationCommand => Noop;
    public ICommand PlanHelmetToArmorMergeCommand => Noop;
    public ICommand RebuildSameKeyCommand => Noop;
    public ICommand ToggleHelmetToArmorMergeCommand => Noop;

    public sealed class VpkTreeStub
    {
        public VpkTreeStub() => Roots = new[] { BuildArmour() };

        public IEnumerable Roots { get; }

        private static StubTreeRow BuildArmour()
        {
            var helmet = new StubTreeRow("头盔", "slot", "helmet")
                .Add(
                    new StubTreeRow("helmet_01", "6 section", "unit").Add(
                        new StubTreeRow("section 0"),
                        new StubTreeRow("section 1"),
                        new StubTreeRow("section 2")),
                    new StubTreeRow("helmet_02", "4 section", "unit"));
            var chest = new StubTreeRow("胸部", "slot", "chest")
                .Add(new StubTreeRow("chest_01", "9 section", "unit"));
            var sturdy = new StubTreeRow("健壮", "bodyType").Add(helmet, chest);
            var root = new StubTreeRow("RE-2310 护甲", "armour").Add(sturdy);
            root.IsExpanded = true;
            sturdy.IsExpanded = true;
            helmet.IsExpanded = true;
            ((StubTreeRow)helmet.Children[0]).IsExpanded = true;
            return root;
        }
    }

    /// <summary>桩行：只实现树控件要求的契约 + 默认行模板会读的展示属性。</summary>
    public sealed class StubTreeRow : HD2ModManager.Controls.Trees.ITreeSurfaceNode
    {
        private readonly List<StubTreeRow> kids = new();
        private static int counter;

        public StubTreeRow(string label, string? subtitle = null, string? pieceType = null)
        {
            Label = label;
            Subtitle = subtitle;
            PieceTypeLabel = pieceType;
            ListKey = $"stub-{++counter}";
        }

        public string Label { get; }
        public string? Subtitle { get; }
        public bool HasSubtitle => !string.IsNullOrEmpty(Subtitle);
        public string? PieceTypeLabel { get; }
        public bool HasPieceTypeLabel => !string.IsNullOrEmpty(PieceTypeLabel);
        public bool IsMixed { get; set; }
        public bool SharedAcrossBodies { get; set; }
        public bool IsChecked { get; set; }
        public bool IsSelected { get; set; }

        public StubTreeRow Add(params StubTreeRow[] children)
        {
            kids.AddRange(children);
            return this;
        }

        public IReadOnlyList<HD2ModManager.Controls.Trees.ITreeSurfaceNode> Children => kids;
        public bool IsExpanded { get; set; }
        public int Depth { get; set; }
        public bool IsAlternateRow { get; set; }
        public int ZIndex { get; set; }
        public string ListKey { get; }
    }
}

internal sealed class AlwaysCommand : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) { }
}
