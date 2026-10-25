using System.Windows;
using System.Windows.Controls;

namespace HD2ModManager.Controls.Cards;

// 卡片内容宿主：把 CardKind 解析成 DataTemplate。
//
// 为什么不用 IValueConverter：模板要允许"页面局部覆盖"。例如 Mod 详情页里那张
// 内嵌 ModListPanel 的卡需要页面代码后置的事件处理，它的模板只能写在页面资源里。
// 只有从自身出发的 TryFindResource 才能先命中页面作用域、再回落到应用级。
//
// 找不到模板时不抛异常、不留白：HasTemplate=false，由 CardStackHost 渲染显式占位。
public sealed class CardBodyPresenter : ContentControl
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind),
        typeof(CardKind),
        typeof(CardBodyPresenter),
        new PropertyMetadata(default(CardKind), OnKindChanged));

    private static readonly DependencyPropertyKey HasTemplatePropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(HasTemplate),
            typeof(bool),
            typeof(CardBodyPresenter),
            new PropertyMetadata(false));

    public static readonly DependencyProperty HasTemplateProperty = HasTemplatePropertyKey.DependencyProperty;

    public CardBodyPresenter()
    {
        Loaded += (_, _) => ResolveTemplate();
    }

    public CardKind Kind
    {
        get => (CardKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public bool HasTemplate => (bool)GetValue(HasTemplateProperty);

    private static void OnKindChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((CardBodyPresenter)d).ResolveTemplate();

    private void ResolveTemplate()
    {
        var key = CardTemplateKeys.For(Kind);
        var template = TryFindResource(key) as DataTemplate;
        ContentTemplate = template;
        SetValue(HasTemplatePropertyKey, template is not null);

        if (template is null)
            System.Diagnostics.Debug.WriteLine($"[Cards] 找不到模板 {key}（{Kind}）：页面作用域与应用资源都没有。");
    }
}
