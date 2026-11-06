namespace HD2ModCore.Domain;

/// <summary>折并计划里的一个成员：当作材质 Option 独立存在的一个 mod。</summary>
/// <param name="NodeId">库节点 id（N 格式的 32 位十六进制）。</param>
/// <param name="ModName">显示名，只用于报告。</param>
/// <param name="PayloadDirectory">它自己的基底目录（<c>sectional/</c>，老库为 <c>vpk2patch/</c>）。</param>
/// <param name="Enabled">折并发生时它是不是启用状态——这就是槽位的初始选中项。</param>
/// <param name="Order">原来的选项顺序（越小越靠前）。</param>
public sealed record MaterialFoldMember(
	string NodeId,
	string ModName,
	string PayloadDirectory,
	bool Enabled,
	int Order);

public enum MaterialFoldBlockerKind
{
	/// <summary>宿主自己的基底目录里没有 unit patch（<c>patch_0</c>），无从下手。</summary>
	HostHasNoUnitPatch = 0,

	/// <summary>这个成员的基底目录里一个 patch 都没有。</summary>
	MemberHasNoPatch,

	/// <summary>这个成员的基底目录里有多个 patch，无法判断该搬哪一个。</summary>
	MemberHasMultiplePatches,
}

public sealed record MaterialFoldBlocker(
	MaterialFoldBlockerKind Kind,
	string? MemberName,
	string Message);

/// <param name="SourcePatchPath">成员目录里那份 patch 的完整路径。</param>
/// <param name="TargetPatchFileName">搬进宿主之后叫什么（<c>&lt;archive&gt;.patch_N</c>）。</param>
public sealed record MaterialFoldEntry(
	string NodeId,
	string ModName,
	string SourcePatchPath,
	string TargetPatchFileName,
	int TargetPatchIndex,
	bool Enabled,
	int Order);

/// <summary>
/// 「把材质 Option 折进宿主」的只读计划。
/// <para>
/// <b>为什么先出计划再动手。</b>这是一次会**移动用户库里文件**的迁移，而"猜错该搬哪一个"
/// 的代价是整个材质变体错位、且不可逆。计划是一次纯计算：它说出会搬哪些、搬到哪个序号、
/// 哪个是启用状态，以及有什么挡着——用户可以在地跑之前先看到这些。
/// </para>
/// </summary>
public sealed record MaterialFoldPlan(
	string HostPayloadDirectory,
	string ArchiveHex16,
	IReadOnlyList<MaterialFoldEntry> Entries,
	IReadOnlyList<MaterialFoldBlocker> Blockers)
{
	/// <summary>没有阻断项且有可搬成员时才允许执行。</summary>
	public bool CanApply => Blockers.Count == 0 && Entries.Count != 0;

	/// <summary>折并之后应当被选中的那一份；没有启用项时为 <c>null</c>。</summary>
	public string? SelectedTargetPatchFileName
		=> Entries.FirstOrDefault(entry => entry.Enabled)?.TargetPatchFileName;
}
