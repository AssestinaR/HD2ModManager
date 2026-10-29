using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace HD2ModManager.Controls.Lists;

// 行内动作列表的唯一生成点：宿主声明（flags）+ 行级抑制 + 宿主文案 → 渲染项。
// 输入固定三项，所以模板里只有一处 MultiBinding，不再有 16 个按钮和 3 个
// "哪个动作在什么条件下可见"的转换器。
//   [0] ModListRowAction   宿主允许的动作 flags
//   [1] 行对象             实现 IModListRowActionFilter 时读取抑制集合
//   [2] IRowActionToolTipSource  可选，宿主对个别动作的更具体文案
public sealed class RowActionListConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 3 || values[0] is not ModListRowAction allowed)
            return Array.Empty<ModListRowActionItem>();

        var hidden = values[1] is IModListRowActionFilter filter
            ? filter.HiddenActions
            : ModListRowAction.None;
        var tips = values[2] as IRowActionToolTipSource;

        var items = new List<ModListRowActionItem>(ModListRowActionCatalog.All.Count);
        foreach (var definition in ModListRowActionCatalog.All)
        {
            if ((allowed & definition.Action) == 0) continue;
            if ((hidden & definition.Action) != 0) continue;
            items.Add(new ModListRowActionItem(
                definition.Action,
                Application.Current?.TryFindResource(definition.IconKey) as Geometry,
                tips?.GetRowActionToolTip(definition.Action) ?? definition.ToolTip,
                definition.StrokeThickness,
                definition.IsDestructive));
        }

        return items;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
