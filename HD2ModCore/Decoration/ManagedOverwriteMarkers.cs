namespace HD2ModCore.Decoration;

/// <summary>
/// 托管 Overwrite 目录的标记文件名。
/// <para>
/// 这些标记是「这个 Overwrite 是管理器生成的」的唯一凭据：
/// <c>ManagedOverwriteDirectory</c> 靠它们决定"能不能覆盖/删除"，
/// 认不出来就拒绝动手（防止把用户手工放进 Overwrite 的文件删掉）。
/// </para>
/// <para>
/// <b>为什么会有多枚。</b>它们分别由不同的派生来源写下，但**归属是同一件事实**：
/// "这个目录归管理器"。所以判定一律"任一即算"，而不是按来源分别判定——
/// 否则换甲写过、头盔合并再写时就会把自己的目录判成"别人的"。
/// </para>
/// </summary>
public static class ManagedOverwriteMarkers
{
    /// <summary>头盔合并生成的 Overwrite。</summary>
    public const string HelmetMerge = ".hd2-helmet-to-armor-overwrite";

    /// <summary>
    /// 换甲（<c>vpk rekey</c>）生成的 Overwrite：把来源 Unit 搬到目标护甲自己的键上。
    /// <para>
    /// 这条派生来源以前**不写任何标记**，于是它的产物在归属判定里是"用户手工文件"——
    /// 一旦部署层改成"只认 Overwrite"，任何一个把 Overwrite 当自己产物的操作都会
    /// 要么拒绝写（用户看到"不是管理器生成"），要么在清理时把整个可部署载荷删掉。
    /// </para>
    /// </summary>
    public const string VpkRekey = ".hd2-vpk-rekey-overwrite";

    /// <summary>
    /// 装饰生成的 Overwrite。<b>装饰包已不再支持</b>（2026-11-04：能力被分件模组取代）。
    /// <para>
    /// 这个标记**必须继续认得**：用户库里可能还留着当年生成的 Overwrite 目录。
    /// 如果我们不再识别它，那条"不是管理器生成就拒绝覆盖/删除"的保护会把它当成用户手工文件，
    /// 于是这些残留目录就再也清不掉了。
    /// </para>
    /// </summary>
    public const string Decoration = ".hd2-decoration-overwrite";

    /// <summary>所有会被认作"管理器生成"的标记。</summary>
    public static IReadOnlyList<string> All { get; } = [HelmetMerge, VpkRekey, Decoration];
}
