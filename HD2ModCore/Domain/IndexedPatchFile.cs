namespace HD2ModCore.Domain;

// 作用：真实磁盘中的一个 patch 文件索引项。
// Purpose: Indexed patch file found on disk.
public sealed record IndexedPatchFile(
	ModNodeId NodeId,
	string FilePath,
	string FileName,
	string ArchiveHex16,
	int SourcePatchIndex,
	int NormalizedOrder,
	PatchSidecarKind SidecarKind,
	long Length,
	DateTimeOffset LastWriteTimeUtc,
	/// <summary>
	/// 部署顺序：同一 Mod、同一归档内，小的先部署、大的后部署（后者覆盖前者）。
	/// <para>
	/// 默认等于 <paramref name="NormalizedOrder"/>；用户在结构文件里给材质选项排过序时，
	/// 这里跟着走——于是"改一个数字"就完成了部署顺序的重定向，patch 文件一个都不用动。
	/// </para>
	/// </summary>
	int DeployOrder = 0);