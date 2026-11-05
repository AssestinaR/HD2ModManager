using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using Xunit;

namespace HD2ModCli.Tests;

/// <summary>
/// `armor-registry apply` 必须和界面走**同一条**实现。
/// <para>
/// 以前它只原子替换协议文件：真映射模式下命令行应用的映射**根本不落到数据层**，
/// 游戏里没有任何效果，而且一句提示都没有。现在它交给
/// <c>RuntimeArmorRegistryService.ApplyAsync</c>，于是协议 / 工作区 / wardrobe.links /
/// 真映射一次做完。
/// </para>
/// <para>
/// 这组用例钉两件事：① 给了 <c>--manager-root</c> + <c>--library</c> 时工作区真的被写；
/// ② <b>素体名字不能被命令行的协议模型抹掉</b>（CLI 的模型里没有 name 字段）。
/// </para>
/// </summary>
[Collection("cli-console")]
public sealed class ArmorRegistryApplySharedPathTests
{
	private static string NewRoot() => Path.Combine(
		Path.GetTempPath(), "hd2-cli-armor-shared-" + Guid.NewGuid().ToString("N"));

	[Fact]
	public async Task ApplyWithLibraryWritesWorkspaceAndKeepsModelNames()
	{
		var root = NewRoot();
		var registryRoot = Path.Combine(root, "registry");
		var managerRoot = Path.Combine(root, "manager");
		var libraryRoot = Path.Combine(root, "library");
		Directory.CreateDirectory(registryRoot);
		Directory.CreateDirectory(managerRoot);
		Directory.CreateDirectory(libraryRoot);
		try
		{
			var paths = new StoragePaths(managerRoot, libraryRoot);
			var service = new RuntimeArmorRegistryService(paths, registryRoot);
			// 预置一份"带名字"的工作区：命令行那份协议模型里没有 name，
			// 如果它被原样写回去，用户就会看到名字变回 key。
			await service.SaveWorkspaceAsync(new RuntimeArmorRegistryDocument(
				1,
				true,
				[new RuntimeArmorRegistryMapping(718698115, null, "custom-a")],
				[new RuntimeArmorRegistryModel(
					"custom-a",
					null,
					[new RuntimeArmorRegistryModelPiece(3, "0000000000000001", 2, 0)],
					"我的素体")]));

			var specPath = Path.Combine(root, "spec.json");
			await File.WriteAllTextAsync(specPath, """
				{
				  "schema": "hd2mod.armor-registry-spec",
				  "enabled": true,
				  "mappings": [ { "targetItemId": 718698115, "modelKey": "custom-a" } ],
				  "models": [
				    { "key": "custom-a", "pieces": [ { "body": 3, "unit": "0000000000000001", "slot": 2, "pieceType": 0 } ] }
				  ]
				}
				""");
			var planPath = Path.Combine(root, "plan.json");
			var plan = await CliTestConsole.RunCapturedAsync(
				"armor-registry", "plan", specPath,
				"--registry-root", registryRoot,
				"--output", planPath,
				"--json");
			Assert.True(plan.ExitCode == 0, "plan 失败：" + plan.StandardOutput + plan.StandardError);

			var apply = await CliTestConsole.RunCapturedAsync(
				"armor-registry", "apply",
				"--plan", planPath,
				"--registry-root", registryRoot,
				"--manager-root", managerRoot,
				"--library", libraryRoot,
				"--confirm",
				"--json");
			Assert.True(apply.ExitCode == 0, "apply 失败：" + apply.StandardOutput + apply.StandardError);

			// ① 工作区被写了（走的是共享实现，而不是只改协议文件）。
			var workspacePath = Path.Combine(libraryRoot, ".hd2modmanager", RuntimeArmorRegistryService.WorkspaceFileName);
			Assert.True(File.Exists(workspacePath));

			// ② 名字还在，revision 前进了。
			var snapshot = await service.ReadSnapshotAsync();
			var model = Assert.Single(snapshot.Document.ModelDefinitions, m => m.Key == "custom-a");
			Assert.Equal("我的素体", model.Name);
			Assert.True(snapshot.Document.Revision > 1);
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}
}
