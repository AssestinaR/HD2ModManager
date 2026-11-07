namespace HD2ModCore.Domain;

/// <summary>
/// 一个 Mod 条目**在磁盘上的存储形态**。它是一组目录事实的函数，不是用户输入，
/// 所以**不落盘**——每次按目录现算，存下来只会多一个会漂的真相。
/// <para>
/// 与 <see cref="ModNodeKind"/> 是两个正交的轴：后者是"节点在库里的角色"
/// （独立 / 某个主体的选项 / 逻辑容器），这里回答的是"它的内容层长什么样"。
/// 以前两者混用一个 <c>kind</c> 字段，于是 CLI 只能报 <c>standard</c>/<c>option</c>，
/// 一个标准的 vpk2patch Mod 被报成 <c>option</c>，"它是怎么来的"完全看不出来。
/// </para>
/// </summary>
public enum ModStorageKind
{
	/// <summary>认不出来：既没有来源也没有内容层。正常库里不该出现。</summary>
	Unknown = 0,

	/// <summary>逻辑容器：根上没有 patch 也没有 vpk，靠结构文件把子级组织起来。</summary>
	Logical,

	/// <summary>传统 Mod：patch 直接放在根上，没有转换产物基底目录。</summary>
	Legacy,

	/// <summary>分件 Mod：根上有来源 patch，另有基底目录（<c>sectional/</c>）承载转换产物。</summary>
	Sectional,

	/// <summary>VPK 转换 Mod：根上是来源 <c>.vpk</c>，基底目录承载转换产物。</summary>
	Vpk2Patch,
}
