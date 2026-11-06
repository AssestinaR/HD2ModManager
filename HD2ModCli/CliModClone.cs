using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

internal sealed record CliModCloneResult(
	bool Ok,
	string? Error,
	string? CloneDirectory,
	string? CloneName,
	string? IdNamespace,
	IReadOnlyList<string> Notes);

/// <summary>
/// `mod clone`：把外观另存成一份**独立命名空间**的副本，于是两套外观能同时在场。
/// <para>
/// <b>为什么必须换命名空间。</b>材质 id = <c>(prefix &lt;&lt; 60) | sha256(命名空间 + 名字)[0..8]</c>。
/// 直接把文件夹复制一份，两份产物的材质 id 逐位相同，进游戏后谁后加载谁赢——
/// 就是"两套外观只能显示一套"。给副本一个新的 <c>--id-namespace</c>，它的 Unit 与材质
/// 才会一起搬到新 id 上，与本体互不相干。
/// </para>
/// <para>
/// <b>为什么要把兄弟 VPK 一起带进来。</b>模型 VPK 往往不含材质，材质在同批转换的
/// 另一份 VPK 里。真机验证过：只喂模型 VPK 会得到完全不同的产物（把 VPK 自带的
/// 材质全烘进一个新的 patch）。所以副本必须是一个自洽的解析集合。
/// </para>
/// </summary>
internal static class CliModClone
{
	/// <summary>从副本里排除的东西：全是派生数据或身份文件。</summary>
	private static readonly HashSet<string> ExcludedDirectories =
		new(StringComparer.OrdinalIgnoreCase)
		{
			ModNodePayloadLayout.VpkPatchDirectoryName,
			ModNodePayloadLayout.LegacyVpkPatchDirectoryName,
			ModNodePayloadLayout.OverwriteDirectoryName,
		};

	/// <summary>身份文件：副本必须有自己的 id、自己的 model key、自己的配置。</summary>
	private static readonly HashSet<string> ExcludedFiles =
		new(StringComparer.OrdinalIgnoreCase)
		{
			"custom-model.json",
			"components.json",
			"wardrobe.json",
			"sections.json",
			"physbone-rig.bin",
			ModSettingsFile.FileName,
		};

	public static async ValueTask<CliModCloneResult> RunAsync(
		CliWorkspace workspace,
		CliLibraryState state,
		string selector,
		string variantName,
		Action<string>? onStage,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(state);
		ArgumentException.ThrowIfNullOrWhiteSpace(variantName);

		var libraryRoot = workspace.LibraryRoot
			?? throw new CliUserException("LibraryNotConfigured", "请用 --library 指定 Mod 库。");
		var managerRoot = workspace.ManagerRoot
			?? throw new CliUserException("ManagerRootRequired", "请用 --manager-root 指定管理器目录。");

		var node = CliProjections.ResolveMod(state.Snapshot.Nodes, selector);
		var sourceDirectory = Path.Combine(libraryRoot, node.RelativePath);
		if (!Directory.Exists(sourceDirectory))
			return Failed($"Mod 目录不存在：{sourceDirectory}");

		var variant = variantName.Trim();
		var cloneName = MakeUniqueFolderName(libraryRoot, $"{node.Metadata.Name} ({variant})");
		var cloneDirectory = Path.Combine(libraryRoot, cloneName);
		var notes = new List<string>();

		onStage?.Invoke($"正在复制 {node.Metadata.Name}…");
		CopyTree(sourceDirectory, cloneDirectory);

		// 兄弟 VPK：同一次转换的材质包，缺了它们材质解析不到。
		var idNamespace = VpkConversionReport.TryRead(sourceDirectory)?.IdNamespace;
		if (!string.IsNullOrWhiteSpace(idNamespace))
		{
			var copied = CopySiblingSources(libraryRoot, node.RelativePath, idNamespace, cloneDirectory);
			if (copied > 0) notes.Add($"一并带上 {copied} 份同批来源 VPK（材质在它们里面）。");
		}

		var outcome = await VpkRebuildService.RunAsync(
			node.Id.Value.ToString("N"),
			cloneDirectory,
			new VpkHostContext(workspace.GameDataRoot, new StoragePaths(managerRoot, libraryRoot)),
			targetArchiveIds: null,
			disabledSections: null,
			onStage,
			cancellationToken,
			idNamespaceOverride: variant).ConfigureAwait(false);

		if (!outcome.Ok)
			return new CliModCloneResult(false, outcome.Error, cloneDirectory, cloneName, null, notes);

		notes.AddRange(outcome.Notes);

		// 副本有自己的身份：新 id + 新名字。它的"名字"就是变体名，用户一眼能分辨。
		var settingsError = ModSettingsFile.TryWrite(cloneDirectory, new ModSettingsFile.Document(
			ModSettingsFile.CurrentSchema,
			EnabledMaterialPatches: null,
			Id: ModNodeId.New().Value.ToString("N"),
			Name: cloneName,
			Notes: null));
		if (settingsError is not null) notes.Add($"副本的设置文件写不进去：{settingsError}");

		return new CliModCloneResult(true, null, cloneDirectory, cloneName, variant, notes);
	}

	private static CliModCloneResult Failed(string error)
		=> new(false, error, null, null, null, Array.Empty<string>());

	private static string MakeUniqueFolderName(string libraryRoot, string desired)
	{
		var name = Sanitize(desired);
		var candidate = name;
		for (var index = 2; Directory.Exists(Path.Combine(libraryRoot, candidate)); index++)
			candidate = $"{name} {index}";
		return candidate;
	}

	private static string Sanitize(string name)
	{
		var invalid = Path.GetInvalidFileNameChars();
		var cleaned = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
		return cleaned.Length == 0 ? "clone" : cleaned;
	}

	private static void CopyTree(string source, string destination)
	{
		Directory.CreateDirectory(destination);
		foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.TopDirectoryOnly))
		{
			if (ExcludedFiles.Contains(Path.GetFileName(file))) continue;
			File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
		}
		foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.TopDirectoryOnly))
		{
			var name = Path.GetFileName(directory);
			if (ExcludedDirectories.Contains(name)) continue;
			// 备份目录是派生的中间产物，不该被复制成"新的一代"。
			if (name.Contains(".bak-", StringComparison.OrdinalIgnoreCase)
				|| name.Contains(".backup-", StringComparison.OrdinalIgnoreCase)) continue;
			CopyDirectory(directory, Path.Combine(destination, name));
		}
	}

	private static void CopyDirectory(string source, string destination)
	{
		Directory.CreateDirectory(destination);
		foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.TopDirectoryOnly))
			File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
		foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.TopDirectoryOnly))
			CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
	}

	/// <summary>把同 <paramref name="idNamespace"/> 的兄弟 Mod 根上的 VPK 复制进副本。</summary>
	private static int CopySiblingSources(
		string libraryRoot,
		string sourceRelativePath,
		string idNamespace,
		string cloneDirectory)
	{
		var copied = 0;
		foreach (var directory in Directory.EnumerateDirectories(libraryRoot))
		{
			if (string.Equals(Path.GetFileName(directory), sourceRelativePath, StringComparison.OrdinalIgnoreCase))
				continue;
			var sibling = VpkConversionReport.TryRead(directory);
			if (sibling is null || !string.Equals(sibling.IdNamespace, idNamespace, StringComparison.Ordinal))
				continue;
			foreach (var vpk in Directory.EnumerateFiles(directory, "*.vpk", SearchOption.TopDirectoryOnly))
			{
				var target = Path.Combine(cloneDirectory, Path.GetFileName(vpk));
				if (File.Exists(target)) continue;
				File.Copy(vpk, target);
				copied++;
			}
		}
		return copied;
	}
}
