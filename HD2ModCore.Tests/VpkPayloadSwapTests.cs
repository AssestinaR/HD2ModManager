using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// 把新产物换进已有 Mod 的 <c>vpk2patch/</c>。
/// <para>
/// 这条路径是"重新导入同一个包 = 更新原 Mod"的落地动作，也是重构建的落地动作。
/// 它踩过的坑全是**静默**的：只读属性让替换失败（错误信息里一个字都没提只读）、
/// 保留文件被一起删掉（素体 key 与 unit id 没了 ⇒ 之前挂好的护甲全部失效）。
/// </para>
/// </summary>
public sealed class VpkPayloadSwapTests : IDisposable
{
	private readonly string root = Path.Combine(
		Path.GetTempPath(), "hd2-payload-swap-" + Guid.NewGuid().ToString("N"));

	public void Dispose()
	{
		try
		{
			if (!Directory.Exists(root)) return;
			// 只读文件删不掉（UnauthorizedAccessException）——这条测试里我们自己把它们标成只读的，
			// 清理前必须自己清回来。管理器侧的 `DeleteNodesWithResultAsync` 也是同一个道理。
			foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
				VpkPayloadSwap.ClearReadOnly(path);
			Directory.Delete(root, recursive: true);
		}
		catch (IOException)
		{
			// 清理失败不该让测试结果变红。
		}
		catch (UnauthorizedAccessException)
		{
			// 同上：删不掉只是留了个临时目录，不是被测行为错了。
		}
	}

	private string NewDirectory(string name)
	{
		var path = Path.Combine(root, name);
		Directory.CreateDirectory(path);
		return path;
	}

	/// <summary>
	/// <b>保留文件必须活下来。</b><c>custom-model.json</c> 记的是"这具素体是谁"（key + unit id），
	/// <c>components.json</c> 记的是"哪些 section 算开关"。换一批产物把它们冲掉，
	/// 用户之前挂好的护甲映射、衣柜里勾的开关就全部指向不存在的东西。
	/// </summary>
	[Fact]
	public void PreservedFilesSurviveAndTheRestIsReplaced()
	{
		var payload = NewDirectory("mod/vpk2patch");
		File.WriteAllText(Path.Combine(payload, "9ba626afa44a3aa3.patch_0"), "旧载荷");
		File.WriteAllText(Path.Combine(payload, "custom-model.json"), "{\"key\":\"custom-a\"}");
		File.WriteAllText(Path.Combine(payload, VpkComponentSwitchFile.FileName), "{}");

		var produced = NewDirectory("produced");
		File.WriteAllText(Path.Combine(produced, "9ba626afa44a3aa3.patch_0"), "新载荷");
		File.WriteAllText(Path.Combine(produced, "9ba626afa44a3aa3.patch_0.gpu_resources"), "新资源");

		var swap = VpkPayloadSwap.Swap(payload, produced, Path.Combine(root, "backup"));

		Assert.True(swap.Succeeded, swap.Error);
		Assert.Equal("新载荷", File.ReadAllText(Path.Combine(payload, "9ba626afa44a3aa3.patch_0")));
		Assert.True(File.Exists(Path.Combine(payload, "9ba626afa44a3aa3.patch_0.gpu_resources")));
		Assert.Equal("{\"key\":\"custom-a\"}", File.ReadAllText(Path.Combine(payload, "custom-model.json")));
		Assert.True(File.Exists(Path.Combine(payload, VpkComponentSwitchFile.FileName)));
	}

	/// <summary>
	/// 旧载荷先备份再删：换一半失败时用户还能回滚，报告里也会给出路径。
	/// </summary>
	[Fact]
	public void TheOldPayloadIsBackedUp()
	{
		var payload = NewDirectory("mod/vpk2patch");
		File.WriteAllText(Path.Combine(payload, "old.patch_0"), "旧载荷");

		var produced = NewDirectory("produced");
		File.WriteAllText(Path.Combine(produced, "new.patch_0"), "新载荷");

		var swap = VpkPayloadSwap.Swap(payload, produced, Path.Combine(root, "backup"));

		Assert.True(swap.Succeeded, swap.Error);
		Assert.NotNull(swap.BackupDirectory);
		Assert.Equal("旧载荷", File.ReadAllText(Path.Combine(swap.BackupDirectory!, "old.patch_0")));
		Assert.False(File.Exists(Path.Combine(payload, "old.patch_0")));
	}

	/// <summary>没有旧产物时不该造出空备份目录（报告里会变成一句莫名其妙的"已备份"）。</summary>
	[Fact]
	public void NoBackupIsCreatedWhenThereIsNothingToBackUp()
	{
		var payload = NewDirectory("mod/vpk2patch");
		var produced = NewDirectory("produced");
		File.WriteAllText(Path.Combine(produced, "a.patch_0"), "新载荷");

		var swap = VpkPayloadSwap.Swap(payload, produced, Path.Combine(root, "backup"));

		Assert.True(swap.Succeeded, swap.Error);
		Assert.Null(swap.BackupDirectory);
	}

	/// <summary>
	/// <b>只读载荷必须能换。</b>库里的 Mod 文件被管理器标成只读，对只读文件
	/// <c>File.Delete</c> / <c>File.Copy(overwrite)</c> 只会得到
	/// <c>Access to the path … is denied</c>——那句话一个字都没提"只读"。
	/// 同时要报告"清过属性"，让调用方换完把保护补回去。
	/// </summary>
	[Fact]
	public void ReadOnlyPayloadCanStillBeReplaced()
	{
		var payload = NewDirectory("mod/vpk2patch");
		var old = Path.Combine(payload, "old.patch_0");
		File.WriteAllText(old, "旧载荷");
		File.SetAttributes(old, File.GetAttributes(old) | FileAttributes.ReadOnly);
		var preserved = Path.Combine(payload, "custom-model.json");
		File.WriteAllText(preserved, "{}");
		File.SetAttributes(preserved, File.GetAttributes(preserved) | FileAttributes.ReadOnly);

		var produced = NewDirectory("produced");
		File.WriteAllText(Path.Combine(produced, "new.patch_0"), "新载荷");

		var swap = VpkPayloadSwap.Swap(payload, produced, Path.Combine(root, "backup"));

		Assert.True(swap.Succeeded, swap.Error);
		Assert.True(swap.RestoreReadOnly);
		Assert.Equal("新载荷", File.ReadAllText(Path.Combine(payload, "new.patch_0")));
		Assert.Equal("{}", File.ReadAllText(preserved));
	}

	/// <summary>补只读：换完之后要把保护状态恢复，否则下一次替换/部署又会遇到同一个坑。</summary>
	[Fact]
	public void SetPayloadReadOnlyMarksEveryFile()
	{
		var payload = NewDirectory("mod/vpk2patch");
		File.WriteAllText(Path.Combine(payload, "a.patch_0"), "x");
		Directory.CreateDirectory(Path.Combine(payload, "nested"));
		File.WriteAllText(Path.Combine(payload, "nested", "b.patch_1"), "y");

		VpkPayloadSwap.SetPayloadReadOnly(payload);

		Assert.All(
			Directory.EnumerateFiles(payload, "*", SearchOption.AllDirectories),
			path => Assert.True((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0, path));
	}

	/// <summary>清只读要能重复调用、对不存在的文件也不抛（替换流程里会连着调好几次）。</summary>
	[Fact]
	public void ClearingReadOnlyIsIdempotentAndForgiving()
	{
		var path = Path.Combine(root, "file.bin");
		Directory.CreateDirectory(root);
		File.WriteAllText(path, "x");
		File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);

		Assert.True(VpkPayloadSwap.ClearReadOnlyForWrite(path));
		Assert.False(VpkPayloadSwap.ClearReadOnlyForWrite(path));
		Assert.False(VpkPayloadSwap.ClearReadOnlyForWrite(Path.Combine(root, "没有这个文件")));
		VpkPayloadSwap.ClearReadOnly(path);
		VpkPayloadSwap.ClearReadOnly(Path.Combine(root, "没有这个文件"));
		Assert.True((File.GetAttributes(path) & FileAttributes.ReadOnly) == 0);
	}
}
