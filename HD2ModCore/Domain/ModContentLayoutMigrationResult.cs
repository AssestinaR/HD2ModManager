namespace HD2ModCore.Domain;

/// <summary>
/// 一次"基底目录改名"的结果。所有计数都是"本次真正改了多少"，便于在日志里核对。
/// </summary>
/// <param name="Renamed">真正从旧名改成新名的 Mod 数。</param>
/// <param name="AlreadyCurrent">本来就是新名的 Mod 数（含没有基底目录的）。</param>
/// <param name="Conflicts">新旧两个目录同时存在的 Mod（**不动**，留给用户/修复命令决定）。</param>
/// <param name="Failures">改名失败的原因（目录被占用、权限不足等）。</param>
public sealed record ModContentLayoutMigrationResult(
	int Renamed,
	int AlreadyCurrent,
	IReadOnlyList<string> Conflicts,
	IReadOnlyList<string> Failures,
	int Adopted = 0)
{
	public bool IsClean => Conflicts.Count == 0 && Failures.Count == 0;

	public static ModContentLayoutMigrationResult Nothing { get; } =
		new(0, 0, Array.Empty<string>(), Array.Empty<string>(), 0);
}
