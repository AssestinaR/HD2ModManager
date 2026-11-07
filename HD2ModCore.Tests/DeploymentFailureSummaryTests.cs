using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

// 作用：验证部署失败摘要：同类错误合并、给出原因与建议、不认识的错误码不被吞掉。
// Purpose: Verifies the deployment failure summary merges same-kind issues, explains them,
// and never hides an unknown error code.
public sealed class DeploymentFailureSummaryTests
{
	private static CoreIssue Error(string code, string message, string? path = null)
		=> new(CoreIssueSeverity.Error, code, message, path);

	[Fact]
	public void Format_CannotDeletePatches_MergesIntoOneLineWithAdvice()
	{
		var data = @"E:\Game\data\";
		var issues = new[]
		{
			Error("CannotDeleteExistingPatch", "Access to the path is denied.", data + "a.patch_0"),
			Error("CannotDeleteExistingPatch", "Access to the path is denied.", data + "a.patch_0.stream"),
			Error("CannotDeleteExistingPatch", "Access to the path is denied.", data + "a.patch_1"),
			Error("CannotDeleteExistingPatch", "Access to the path is denied.", data + "a.patch_1.stream"),
		};

		var text = DeploymentFailureSummary.Format("活动配置部署失败", issues);

		Assert.Contains("被其他程序占用", text);
		Assert.Contains("共 4 个文件", text);
		Assert.Contains("a.patch_0", text);
		Assert.Contains("等", text);
		Assert.Contains("关闭游戏", text);
		Assert.DoesNotContain(@"E:\Game\data", text);
		Assert.Single(text.Split('\n'), line => line.Contains("被其他程序占用"));
	}

	[Fact]
	public void Format_UnknownCode_IsShownVerbatim()
	{
		var text = DeploymentFailureSummary.Format("失败", [Error("BrandNewProblem", "something odd")]);

		Assert.Contains("[BrandNewProblem] something odd", text);
	}

	[Fact]
	public void Format_NoErrors_PointsToLog()
	{
		var text = DeploymentFailureSummary.Format("失败", [new CoreIssue(CoreIssueSeverity.Warning, "W", "w", null)]);

		Assert.Contains("部署日志", text);
	}
}
