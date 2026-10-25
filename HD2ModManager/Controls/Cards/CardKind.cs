namespace HD2ModManager.Controls.Cards;

// 一张卡片的内容种类。
//
// 这是逻辑层与控件层之间唯一需要共享的"内容标识"：
//   - 逻辑层只允许从这里取值（编译期安全，写错就是编译错误）；
//   - 控件层按约定在 Themes/CardTemplates.xaml 里提供 CardTemplate.{Kind} 模板；
//   - 想查"项目里一共有哪些卡片"，只读这一个文件，不需要打开任何 XAML。
//
// 新增一种卡片 = 这里加一个值 + 模板字典里加一份模板。缺模板时宿主会渲染
// 一个显式的"缺少模板"占位，并在 Debug 输出里告警，不会静默留白。
public enum CardKind
{
    // 通用内容
    Text,
    Notice,
    KeyValueList,

    // Mod 详情页
    ModHeader,
    ModDescription,
    ModActions,
    VpkActions,
    VpkComponents,
    DecorationDetails,
    UserStatus,
    AssetTags,
    ModelCompatibility,
}
