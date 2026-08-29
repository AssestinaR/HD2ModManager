namespace HD2ModCore.Domain;

// 作用：部署计划操作类型。
// Purpose: Operation kind for apply plans.
public enum ApplyOperationKind
{
	DeletePatch,
	DeployPatch,
	DeleteExternalFile,
	DeployExternalFile,
	WriteState,
}
