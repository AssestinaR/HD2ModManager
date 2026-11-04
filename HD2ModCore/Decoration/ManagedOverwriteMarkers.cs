namespace HD2ModCore.Decoration;

/// <summary>
/// 托管 Overwrite 目录的标记文件名。
/// <para>
/// 这些标记是「这个 Overwrite 是管理器生成的」的唯一凭据：
/// <c>HostDerivedOverwriteService</c> 靠它们决定"能不能覆盖/删除"，
/// 认不出来就拒绝动手（防止把用户手工放进 Overwrite 的文件删掉）。
/// </para>
/// </summary>
public static class ManagedOverwriteMarkers
{
    /// <summary>头盔合并生成的 Overwrite。</summary>
    public const string HelmetMerge = ".hd2-helmet-to-armor-overwrite";

    /// <summary>
    /// 装饰生成的 Overwrite。<b>装饰包已不再支持</b>（2026-11-04：能力被分件模组取代）。
    /// <para>
    /// 这个标记**必须继续认得**：用户库里可能还留着当年生成的 Overwrite 目录。
    /// 如果我们不再识别它，那条"不是管理器生成就拒绝覆盖/删除"的保护会把它当成用户手工文件，
    /// 于是这些残留目录就再也清不掉了。
    /// </para>
    /// </summary>
    public const string Decoration = ".hd2-decoration-overwrite";
}
