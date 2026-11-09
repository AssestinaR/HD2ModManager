using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

/// <param name="CloneDirectory">副本的完整路径。</param>
/// <param name="CloneName">副本的文件夹名，同时也是它的显示名。</param>
/// <param name="IdNamespace">副本使用的新命名空间（变体名）。</param>
public sealed record ModCloneResult(
	bool Ok,
	string? Error,
	string? CloneDirectory,
	string? CloneName,
	string? IdNamespace,
	IReadOnlyList<string> Notes)
{
	public static ModCloneResult Failed(string error, IReadOnlyList<string>? notes = null)
		=> new(false, error, null, null, null, notes ?? Array.Empty<string>());
}

/// <summary>
/// 把一个 Mod 复制成**独立命名空间**的变体，于是两套外观能同时在场。
/// <para>
/// <b>为什么必须换命名空间。</b>材质 id = <c>(prefix &lt;&lt; 60) | sha256(命名空间 + 名字)[0..8]</c>。
/// 直接把文件夹复制一份，两份产物的材质 id 逐位相同，进游戏后谁后加载谁赢——
/// 就是"两套外观只能显示一套"。给副本一个新的命名空间，它的 Unit 与材质
/// 才会一起搬到新 id 上，与本体互不相干。
/// </para>
/// <para>
/// <b>为什么要把同族 VPK 一起带进来。</b>模型 VPK 往往不含材质，材质在同批转换的
/// 另一份 VPK 里。真机验证过：只喂模型 VPK 会得到完全不同的产物（把 VPK 自带的
/// 材质全烘进一个新的 patch）。所以副本必须是一个自洽的解析集合。
/// </para>
/// <para>
/// 这里是**一份实现、两个入口**（CLI 的 `mod clone` 与详情页的「复制为变体」）：
/// 复制逻辑写在两处的话，一处改了另一处就悄悄不一样了。
/// </para>
/// </summary>
public static class ModCloneService
{
	/// <summary>从副本里排除的东西：全是派生数据。</summary>
	private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
	{
		ModNodePayloadLayout.VpkPatchDirectoryName,
		ModNodePayloadLayout.LegacyVpkPatchDirectoryName,
		ModNodePayloadLayout.OverwriteDirectoryName,
	};

	/// <summary>身份文件：副本必须有自己的 id、自己的 model key、自己的配置。</summary>
	private static readonly HashSet<string> ExcludedFiles = new(StringComparer.OrdinalIgnoreCase)
	{
		"custom-model.json",
		"components.json",
		"wardrobe.json",
		"sections.json",
		ModSettingsFile.FileName,
	};

	/// <summary>
	/// 副本要不要跳过这个文件。物理旁车（<c>physbone-*.bin</c>）一律不复制：
	/// 它们是从源模型派生出来的，副本得按自己的配置重新产出。
	/// 按前缀认而不是写死名字，免得以后新增旁车时被悄悄复制进副本、把两份参数混在一起。
	/// </summary>
	private static bool IsExcludedFile(string fileName)
		=> PhysBoneAuxFiles.IsAuxFile(fileName) || ExcludedFiles.Contains(fileName);

	/// <param name="sourceDirectory">被复制的 Mod 目录。</param>
	/// <param name="sourceDisplayName">它的显示名，用来拼副本名「名字 (变体)」。</param>
	/// <param name="sourceRelativePath">它在库根下的相对路径（用于跳过自己）。</param>
	/// <param name="variantName">变体名，同时作为新命名空间与副本名后缀。</param>
	public static async ValueTask<ModCloneResult> CloneAsync(
		string libraryRoot,
		string managerRoot,
		string? gameDataDirectory,
		string sourceDirectory,
		string sourceDisplayName,
		string sourceRelativePath,
		string variantName,
		Action<string>? onStage = null,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
		ArgumentException.ThrowIfNullOrWhiteSpace(managerRoot);
		ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
		ArgumentException.ThrowIfNullOrWhiteSpace(variantName);
		if (!Directory.Exists(sourceDirectory))
			return ModCloneResult.Failed($"Mod 目录不存在：{sourceDirectory}");

		var variant = variantName.Trim();
		var cloneName = MakeUniqueFolderName(libraryRoot, $"{sourceDisplayName} ({variant})");
		var cloneDirectory = Path.Combine(libraryRoot, cloneName);
		var notes = new List<string>();

		onStage?.Invoke($"正在复制 {sourceDisplayName}…");
		CopyTree(sourceDirectory, cloneDirectory);

		// 同族 VPK：同一批转换的材质包，缺了它们材质解析不到。
		var idNamespace = VpkConversionReport.TryRead(sourceDirectory)?.IdNamespace;
		if (!string.IsNullOrWhiteSpace(idNamespace))
		{
			var copied = CopySiblingSources(libraryRoot, sourceRelativePath, idNamespace, cloneDirectory);
			if (copied > 0) notes.Add($"一并带上 {copied} 份同批来源 VPK（材质在它们里面）。");
		}

		var outcome = await VpkRebuildService.RunAsync(
			sourceRelativePath,
			cloneDirectory,
			new VpkHostContext(gameDataDirectory, new StoragePaths(managerRoot, libraryRoot)),
			targetArchiveIds: null,
			disabledSections: null,
			onStage,
			cancellationToken,
			idNamespaceOverride: variant).ConfigureAwait(false);

		if (!outcome.Ok)
			return new ModCloneResult(false, outcome.Error, cloneDirectory, cloneName, null, notes);

		notes.AddRange(outcome.Notes);

		// 副本有自己的身份：新 id + 新名字。
		var settingsError = ModSettingsFile.TryWrite(cloneDirectory, new ModSettingsFile.Document(
			ModSettingsFile.CurrentSchema,
			EnabledMaterialPatches: null,
			Id: ModNodeId.New().Value.ToString("N"),
			Name: cloneName,
			Notes: null));
		if (settingsError is not null) notes.Add($"副本的设置文件写不进去：{settingsError}");

		return new ModCloneResult(true, null, cloneDirectory, cloneName, variant, notes);
	}

	/// <summary>
	/// 给一次复制取一个还没被用过的变体名：「变体1」「变体2」……
	/// <para>
	/// 变体名同时是**新命名空间**，所以不能重复——两个副本用同一个名字，它们的材质 id
	/// 会再次相同，"两套外观同时在场"就不成立了。
	/// </para>
	/// </summary>
	public static string NextVariantName(string libraryRoot, string sourceDisplayName)
	{
		var prefix = sourceDisplayName + " (";
		var used = new HashSet<int>();
		foreach (var directory in Directory.EnumerateDirectories(libraryRoot))
		{
			var name = Path.GetFileName(directory);
			if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
			var inner = name[prefix.Length..].TrimEnd(')');
			var digits = inner.StartsWith("变体", StringComparison.Ordinal) ? inner["变体".Length..] : inner;
			if (int.TryParse(digits, out var number)) used.Add(number);
		}
		for (var index = 1; ; index++)
			if (!used.Contains(index)) return "变体" + index;
	}

	/// <summary>库根下已经没有同名目录时就用这个，否则依次加 " 2"、" 3"。</summary>
	public static string MakeUniqueFolderName(string libraryRoot, string desired)
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
			if (IsExcludedFile(Path.GetFileName(file))) continue;
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
