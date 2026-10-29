using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace HD2ModManager.Controls.Actions;

// 动作的身份。逻辑层只说"这是哪种动作"，图标、颜色、尺寸由控件层决定。
// 与 RowIcon.* / NavIcon.* / EmptyIcon.* 同一套约定：几何在应用级资源里，
// 控件按枚举取。加一种动作 = 一个枚举值 + 一条 catalog + 一个几何资源。
public enum PageActionKind
{
    None = 0,
    CreateProfile,
    Import,
    SwitchProfile,
    ActivateProfile,
    ScheduleDeployment,
    DeactivateProfile,
    RenameProfile,
    RemoveProfile,
    RefreshProfile,
    Refresh,
    ValidateLibrary,
    Export,
    CreateLogicalMod,
    OpenFolder,
    ArmorCreateModel,
    // 详情页
    Table,
    Image,
    EditName,
    EditDescription,
    Fix,
    CrossArmor,
    Compact,
    Package,
    BatchDecoration,
    DecorationPlan,
    Helmet,
    Merge,
    CustomModel,
    EditDecoration,
    PatchRebuild,
}

// 语义色调。逻辑层只说"这是主操作 / 普通操作 / 危险操作"，
// 具体颜色由控件层映射，和行内动作 pill 的处理一致。
public enum PageActionTone
{
    Neutral,
    Primary,
    Danger,
}

internal static class PageActionIconCatalog
{
    // 别名共用一个几何：Import 和 CreateProfile 是同一个"加号"，
    // Refresh 和 RefreshProfile 是同一个"刷新"。
    public static string IconKey(PageActionKind kind) => kind switch
    {
        PageActionKind.CreateProfile => "ActionIcon.Plus",
        PageActionKind.Import => "ActionIcon.Plus",
        PageActionKind.ArmorCreateModel => "ActionIcon.Plus",
        PageActionKind.SwitchProfile => "ActionIcon.Switch",
        PageActionKind.ActivateProfile => "ActionIcon.Check",
        PageActionKind.ScheduleDeployment => "ActionIcon.Check",
        PageActionKind.DeactivateProfile => "ActionIcon.Cross",
        PageActionKind.RenameProfile => "ActionIcon.Rename",
        PageActionKind.RemoveProfile => "ActionIcon.Delete",
        PageActionKind.RefreshProfile => "ActionIcon.Refresh",
        PageActionKind.Refresh => "ActionIcon.Refresh",
        PageActionKind.ValidateLibrary => "ActionIcon.Validate",
        PageActionKind.Export => "ActionIcon.Export",
        PageActionKind.CreateLogicalMod => "ActionIcon.FolderPlus",
        PageActionKind.OpenFolder => "ActionIcon.Folder",
        PageActionKind.Table => "ActionIcon.Table",
        PageActionKind.Image => "ActionIcon.Image",
        PageActionKind.EditName => "ActionIcon.Rename",
        PageActionKind.EditDescription => "ActionIcon.Note",
        PageActionKind.Fix => "ActionIcon.Fix",
        PageActionKind.CrossArmor => "ActionIcon.Switch",
        PageActionKind.Compact => "ActionIcon.Compact",
        PageActionKind.Package => "ActionIcon.Package",
        PageActionKind.BatchDecoration => "ActionIcon.HangerPlus",
        PageActionKind.DecorationPlan => "ActionIcon.HangerPlus",
        PageActionKind.Helmet => "ActionIcon.Helmet",
        PageActionKind.Merge => "ActionIcon.Merge",
        PageActionKind.CustomModel => "ActionIcon.CustomModel",
        PageActionKind.EditDecoration => "ActionIcon.Hanger",
        PageActionKind.PatchRebuild => "ActionIcon.Refresh",
        _ => "ActionIcon.Plus",
    };
}

// Kind → 几何。
public sealed class PageActionIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => value is PageActionKind kind
            ? Application.Current?.TryFindResource(PageActionIconCatalog.IconKey(kind)) as Geometry
            : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}

// Tone → 画刷。parameter 选部位：Background / Foreground / Border。
// 直接映射到既有 token，不新增一层别名资源。
public sealed class PageActionToneBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        var tone = value is PageActionTone t ? t : PageActionTone.Neutral;
        var part = parameter as string ?? "Background";
        var key = (tone, part) switch
        {
            (PageActionTone.Primary, "Foreground") => "ActionTone.OnAccent",
            (PageActionTone.Primary, "Border") => "Accent",
            (PageActionTone.Primary, _) => "Accent",
            (PageActionTone.Danger, "Foreground") => "ActionTone.OnAccent",
            (PageActionTone.Danger, "Border") => "StatusDanger",
            (PageActionTone.Danger, _) => "StatusDanger",
            (_, "Foreground") => "TextPrimary",
            (_, "Border") => "StrokeSubtle",
            _ => "SurfaceElevated",
        };
        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}

// 高度 → 胶囊圆角：圆片被挤压时仍然是个干净的胶囊，而不是圆角对不上的方块。
public sealed class CapsuleCornerRadiusConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => value is double height && height > 0
            ? new CornerRadius(height / 2d)
            : new CornerRadius(0);

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}
