using HD2ModCore.Domain;

namespace HD2ModCore.Application;

// 作用：为 ProfileApplyService 等应用流程提供可复用的外部安装文件事实。
// Purpose: Supplies reusable external install file facts to application workflows such as ProfileApplyService.
public interface IExternalInstallFileFactsService
{
	ValueTask<ExternalInstallDetectionResult> DetectAsync(
		string sourceRootDirectory,
		CancellationToken cancellationToken = default);
}
