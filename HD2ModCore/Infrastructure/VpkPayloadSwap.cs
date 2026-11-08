using System.IO;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// 把新转换出来的产物**整批换进**某个已有 Mod 的 <c>vpk2patch/</c>。
/// <para>
/// <b>为什么单独抽出来。</b>两条路都要做同一件事：重构建（详情页那颗按钮）与
/// "同一来源再来一次"的导入（重新导入同一个 VPK 应当更新原有 Mod，而不是再建一个
/// 长得一模一样的）。这两条路的危险点完全一样，所以规则只能有一份：
/// </para>
/// <list type="number">
/// <item>
/// <b>只读属性。</b>库里的 Mod 文件被管理器标成只读，对只读文件 <c>File.Delete</c> /
/// <c>File.Copy(overwrite)</c> 只会得到 <c>Access to the path … is denied</c>——
/// 那句话一个字都没提"只读"，第一眼必然怀疑权限或占用。所以动手前逐个清属性，替换完再补回去。
/// </item>
/// <item>
/// <b>保留文件。</b><see cref="PreservedFileNames"/> 里的文件既不备份也不删：
/// 它们记的是"这具素体是谁"（<c>custom-model.json</c> 的 key / unit id）与
/// "哪些 section 算开关"（<c>components.json</c>），换一批产物不该把它们冲掉。
/// </item>
/// <item>
/// <b>先备份再删。</b>换一半失败时旧产物还在备份目录里，报告里会给出路径。
/// </item>
/// </list>
/// </summary>
public static class VpkPayloadSwap
{
	/// <summary>换载荷时既不备份也不删的文件名。</summary>
	public static readonly string[] PreservedFileNames = ["custom-model.json", SectionGroupFile.FileName];

	/// <summary>一次换载荷的结果。</summary>
	/// <param name="Succeeded">是否换成功（失败时旧产物已经还原到原处）。</param>
	/// <param name="BackupDirectory">旧产物的备份目录；没有旧产物时为 <c>null</c>。</param>
	/// <param name="RestoreReadOnly">原来有只读文件被清过属性，替换完要补回去。</param>
	/// <param name="Error">失败原因（已包含回滚提示）。</param>
	public sealed record Result(bool Succeeded, string? BackupDirectory, bool RestoreReadOnly, string? Error)
	{
		public static Result Failure(string error) => new(false, null, false, error);
	}

	public static Result Swap(
		string payloadDirectory,
		string producedDirectory,
		string backupRoot)
	{
		var backupDirectory = (string?)null;
		var restoreReadOnly = false;
		try
		{
			Directory.CreateDirectory(payloadDirectory);
			var existing = Directory.EnumerateFiles(payloadDirectory, "*", SearchOption.TopDirectoryOnly)
				.Where(path => !PreservedFileNames.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
				.ToArray();

			// 先清只读（备份的同时做，备份文件也保持可读即可）。
			foreach (var path in existing)
			{
				var attributes = File.GetAttributes(path);
				if ((attributes & FileAttributes.ReadOnly) == 0) continue;
				restoreReadOnly = true;
				File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
			}

			if (existing.Length != 0)
			{
				Directory.CreateDirectory(backupRoot);
				foreach (var path in existing)
				{
					var destination = Path.Combine(backupRoot, Path.GetFileName(path));
					File.Copy(path, destination, overwrite: true);
					// 备份也清掉只读：它是给人回滚用的，不该再挡一次删除。
					ClearReadOnly(destination);
				}

				backupDirectory = backupRoot;
			}

			foreach (var path in existing) File.Delete(path);
			foreach (var path in Directory.EnumerateFiles(producedDirectory, "*", SearchOption.TopDirectoryOnly))
			{
				var name = Path.GetFileName(path);
				var destination = Path.Combine(payloadDirectory, name);
				// **保留下来的用户文件不被新产物覆盖**：`custom-model.json` 记的是"这具素体是谁"
				// （key 与 unit id 基），`components.json` 记的是"哪些部件算开关"。
				// 它们留着不删、还让新产物盖掉，等于这一步白做——用户改过的组件树会被转换器的
				// 默认版本冲掉，而界面上看不出任何变化。
				if (PreservedFileNames.Contains(name, StringComparer.OrdinalIgnoreCase) && File.Exists(destination))
					continue;
				// 保留下来的文件同样是只读的 —— 覆盖前也得清。
				ClearReadOnly(destination);
				File.Copy(path, destination, overwrite: true);
			}

			// ⚠️ **这里不写 patches.json。**
			//
			// 结构文件的内容是"哪个 patch 是谁"，唯一来源是**转换报告**；而调用方在这之后才把
			// 新报告写进 Mod 根（`vpk2patch.json`）。在这里写，用的是**上一份**报告：
			// 合并了几个材质变体之后，旧报告里根本没有那几个 patch → 清单里材质选项名全是空的，
			// 界面退化成"材质 1 / 2 / 3"（2026-11-08 用户报的就是这个）。
			// 现在统一由调用方在写完报告之后调 `SectionalPatchManifestFile.Rebuild`。

			return new Result(true, backupDirectory, restoreReadOnly, null);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			var error = "替换 vpk2patch/ 失败：" + exception.Message
				+ (restoreReadOnly ? "（这些文件是只读的，我已经先清过一次属性；如果还失败，多半是别的程序占着它）" : string.Empty)
				+ (backupDirectory is null ? string.Empty : $"（旧产物的备份在：{backupDirectory}）");
			return new Result(false, backupDirectory, restoreReadOnly, error);
		}
	}

	/// <summary>把整个 <c>vpk2patch/</c> 标回只读（恢复库的保护状态）；标不上不抛。</summary>
	public static void SetPayloadReadOnly(string payloadDirectory)
	{
		try
		{
			if (!Directory.Exists(payloadDirectory)) return;
			foreach (var path in Directory.EnumerateFiles(payloadDirectory, "*", SearchOption.AllDirectories))
				MarkReadOnly(path);
		}
		catch (IOException) { }
		catch (UnauthorizedAccessException) { }
	}

	/// <summary>
	/// 写文件前把只读属性清掉；返回"原本是只读的"（写完由调用方补回去）。
	/// </summary>
	public static bool ClearReadOnlyForWrite(string path)
	{
		try
		{
			if (!File.Exists(path)) return false;
			var attributes = File.GetAttributes(path);
			if ((attributes & FileAttributes.ReadOnly) == 0) return false;
			File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
			return true;
		}
		catch (IOException) { return false; }
		catch (UnauthorizedAccessException) { return false; }
	}

	/// <summary>把文件重新标成只读（恢复库的保护状态）；标不上也不抛。</summary>
	public static void MarkReadOnly(string path)
	{
		try { File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly); }
		catch (IOException) { }
		catch (UnauthorizedAccessException) { }
	}

	/// <summary>清掉一个文件的只读属性；文件不存在或清不掉都不抛。</summary>
	public static void ClearReadOnly(string path)
	{
		try
		{
			if (!File.Exists(path)) return;
			var attributes = File.GetAttributes(path);
			if ((attributes & FileAttributes.ReadOnly) == 0) return;
			File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
		}
		catch (IOException) { }
		catch (UnauthorizedAccessException) { }
	}
}
