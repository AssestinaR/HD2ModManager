using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using HD2ModManager.Controls.Cards;

namespace HD2ModManager.ViewModels;

// 作用：Mod 详情页的卡片声明。
//
// 这一层只回答"有哪些卡片、顺序如何、什么时候可见"，不回答"长什么样"：
// 外观在 ModDetailsPageView.xaml 的 CardTemplate.* 模板里。
// 页面之所以能看到卡片，是因为这里把 CardDescriptor 递给 CardStackHost。
public sealed record ModDetailsCardState(
    bool HasDescription,
    bool CanUsePatchTools,
    bool ShowPatchSettingsCard,
    bool IsDecoration,
    bool CanShowDecorationPlan,
    bool IsVpkMod,
    bool ShowVpkComponentCard,
    bool ShowUserStatusCard,
    bool ShowOutdatedCompatibility);

public static class ModDetailsCardLayout
{
    public const string HeaderCardId = "mod-header";
    public const string DescriptionCardId = "mod-description";
    public const string ActionsCardId = "mod-actions";
    public const string VpkActionsCardId = "vpk-actions";
    public const string VpkComponentsCardId = "vpk-components";
    public const string DecorationCardId = "decoration-details";
    public const string UserStatusCardId = "user-status";
    public const string AssetTagsCardId = "asset-tags";
    public const string CompatibilityCardId = "model-compatibility";

    public static IReadOnlyList<CardDescriptor> Create(object data, ICommand applyVpkComponentsCommand)
    {
        ArgumentNullException.ThrowIfNull(data);

        return new[]
        {
            new CardDescriptor(HeaderCardId, CardKind.ModHeader, data) { Order = 10 },
            new CardDescriptor(DescriptionCardId, CardKind.ModDescription, data) { Order = 20, Title = "备注" },
            new CardDescriptor(ActionsCardId, CardKind.ModActions, data) { Order = 30 },
            new CardDescriptor(VpkActionsCardId, CardKind.VpkActions, data) { Order = 40, Title = "VPK 动作" },
            new CardDescriptor(VpkComponentsCardId, CardKind.VpkComponents, data)
            {
                Order = 50,
                Title = "衣物装饰组件",
                HeaderContent = new CardHeaderAction("应用", applyVpkComponentsCommand)
            },
            new CardDescriptor(DecorationCardId, CardKind.DecorationDetails, data) { Order = 60, Title = "装饰详情" },
            new CardDescriptor(UserStatusCardId, CardKind.UserStatus, data) { Order = 70, Title = "当前状态" },
            new CardDescriptor(AssetTagsCardId, CardKind.AssetTags, data) { Order = 80, Title = "资产标签" },
            new CardDescriptor(CompatibilityCardId, CardKind.ModelCompatibility, data) { Order = 90, Title = "模型兼容性" },
        };
    }

    // 可见性只由状态决定；宿主监听 IsVisible 变化并做重排动画。
    public static void Sync(IEnumerable<CardDescriptor> cards, ModDetailsCardState state)
    {
        ArgumentNullException.ThrowIfNull(cards);
        ArgumentNullException.ThrowIfNull(state);

        foreach (var card in cards)
        {
            card.IsVisible = card.Id switch
            {
                HeaderCardId => true,
                DescriptionCardId => state.HasDescription,
                ActionsCardId => true,
                VpkActionsCardId => state.IsVpkMod,
                VpkComponentsCardId => state.ShowVpkComponentCard,
                DecorationCardId => state.IsDecoration,
                UserStatusCardId => state.ShowUserStatusCard,
                AssetTagsCardId => state.CanUsePatchTools,
                CompatibilityCardId => state.ShowOutdatedCompatibility,
                _ => card.IsVisible
            };
        }
    }
}

public partial class ModDetailsPageViewModel
{
    private ObservableCollection<CardDescriptor>? _cards;
    private bool _cardsSubscribed;

    // 惰性构建：页面第一次绑定 Cards 时才创建，并把自身属性变化接进可见性同步。
    // 这样不用改动构造函数，也不会在没人看详情页时产生开销。
    public ObservableCollection<CardDescriptor> Cards
    {
        get
        {
            if (_cards is null)
            {
                _cards = new ObservableCollection<CardDescriptor>(
                    ModDetailsCardLayout.Create(this, ApplyVpkComponentsCommand));
                ModDetailsCardLayout.Sync(_cards, BuildCardState());
            }

            if (!_cardsSubscribed)
            {
                _cardsSubscribed = true;
                PropertyChanged += OnCardStatePropertyChanged;
            }

            return _cards;
        }
    }

    private ModDetailsCardState BuildCardState() => new(
        HasDescription: HasDescription,
        CanUsePatchTools: CanUsePatchTools,
        ShowPatchSettingsCard: ShowPatchSettingsCard,
        IsDecoration: IsDecoration,
        CanShowDecorationPlan: CanShowDecorationPlan,
        IsVpkMod: IsVpkMod,
        ShowVpkComponentCard: ShowVpkComponentCard,
        ShowUserStatusCard: ShowUserStatusCard,
        ShowOutdatedCompatibility: ShowOutdatedCompatibility);

    private void OnCardStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_cards is null) return;
        ModDetailsCardLayout.Sync(_cards, BuildCardState());
    }
}
