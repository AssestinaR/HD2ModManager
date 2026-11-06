using HD2ModCore.Decoration;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// 用户对外观做的一次选择。<b>现在是参数、还没有落盘</b>——持久化会随 `mod.json` 一起做（P4）。
/// </summary>
/// <param name="EnabledMaterialPatchFileNames">
/// 要启用的材质 patch 文件名集合——**每个材质选项一个 patch，可以任意启用几个**。
/// <para>
/// <c>null</c> = 读 Mod 自己的设置文件（`<mod>/.hd2modmanager-mod.json`）；那里也没有时，
/// 默认只启用宿主自己那次转换产出的材质（「模型内置材质」）。
/// 空集合 = 一个都不启用（用户明确关掉了全部材质）。
/// </para>
/// <para>
/// <b>不做单选限制。</b>多个材质包同时启用时，材质 id 之间可能互相冲突——那是包作者之间的事，
/// 不是管理器该替用户决定的事。用户勾了哪些就诚实部署哪些，和今天多个 Option 同时启用一样。
/// </para>
/// </param>
public sealed record OverwriteSelection(IReadOnlyCollection<string>? EnabledMaterialPatchFileNames)
{
	/// <summary>用默认（只启用模型内置材质）。</summary>
	public static OverwriteSelection Default { get; } = new((IReadOnlyCollection<string>?)null);

	/// <summary>一个材质都不启用。</summary>
	public static OverwriteSelection Empty { get; } = new(Array.Empty<string>());
}

public sealed record OverwriteMaterializationResult(
	bool Succeeded,
	IReadOnlyList<string> WrittenFiles,
	IReadOnlyList<string> Warnings,
	string? Error)
{
	public static OverwriteMaterializationResult Failure(string error, IReadOnlyList<string>? warnings = null)
		=> new(false, Array.Empty<string>(), warnings ?? Array.Empty<string>(), error);
}

/// <summary>
/// 把「基底目录里的 patch」+「用户的选择」组装成 <c>Overwrite/</c>——**唯一**写这一层的地方。
/// <para>
/// <b>组装规则（三层，缺一层就出黑模）。</b>
/// <list type="number">
/// <item><b>unit 层</b>：<c>patch_0</c>，或被换甲替换过的那份。</item>
/// <item><b>基底材质层</b>：<b>宿主自己的来源</b>产出的材质 patch。
/// 实测例子：<c>恋星milfy_bill</c> 自己那次转换同时产出了 <c>unitPatch=patch_0</c> 与
/// <c>materialPatch=patch_1</c>——它们是**同一份来源**的基底材质，不是变体。
/// 把它们漏掉，unit 引用的材质 id 就没人定义，游戏里就是黑模。</item>
/// <item><b>变体层</b>：用户选中的那一份材质 patch。没选中的留在基底目录里，不进来、也就不部署。</item>
/// </list>
/// </para>
/// <para>
/// <b>基底与变体怎么区分。</b>看宿主**自己的转换报告**：报告 <c>patches[]</c> 里的
/// <c>unitPatch</c> / <c>materialPatch</c> 就是这个 Mod 自己来源产出的 patch，属基底；
/// 其余（折并进来的）属变体。报告缺失时退化为"只有 <c>patch_0</c> 是基底"。
/// </para>
/// <para>
/// <b>为什么必须是唯一写者。</b>以前换甲直接往 Overwrite 里写、物化又是另一条路，
/// 两套结果会互相覆盖：物化器把换甲产物换回未换甲的基底 patch，用户选的护甲就失效了。
/// 现在换甲产出一份"unit 层"交给这里，由这里统一组装。
/// </para>
/// <para>
/// <b>失败时不留半成品。</b>走 <see cref="ManagedOverwriteDirectory"/> 的备份与回滚：
/// 上一次的 Overwrite 会原样回来，用户看到的外观不会因为一次失败的切换而变成空白。
/// </para>
/// </summary>
public static class OverwriteMaterializer
{
	/// <summary>基底目录里 unit 的固定序号名（<c>&lt;archive&gt;.patch_0</c>）。</summary>
	public const int UnitPatchIndex = 0;

	public static OverwriteMaterializationResult Materialize(
		string modDirectory,
		OverwriteSelection selection,
		string? unitLayerPatchPath = null,
		string marker = ManagedOverwriteMarkers.VpkRekey)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(modDirectory);
		ArgumentNullException.ThrowIfNull(selection);

		var payload = ModNodePayloadLayout.TryGetExistingVpkPatchDirectory(modDirectory);
		if (payload is null)
			return OverwriteMaterializationResult.Failure(
				"这个 Mod 没有基底目录（sectional/），没有可物化的东西。");

		var archive = SectionalPatchManifestFile.ReadArchiveHex16(payload);
		if (string.IsNullOrEmpty(archive))
			return OverwriteMaterializationResult.Failure("基底目录里没有 unit patch，无法物化生效层。");

		var report = VpkConversionReport.TryRead(modDirectory);
		var unitFileName = $"{archive}.patch_{UnitPatchIndex}";

		// unit 层：默认取基底里那份；调用方给了替换（换甲产物）就用它。
		var unitLayer = string.IsNullOrWhiteSpace(unitLayerPatchPath)
			? Path.Combine(payload, unitFileName)
			: unitLayerPatchPath;
		if (!File.Exists(unitLayer))
			return OverwriteMaterializationResult.Failure($"unit 层 patch 不存在：{unitLayer}");

		var warnings = new List<string>();
		var enabled = selection.EnabledMaterialPatchFileNames
			// 没有显式选择 ⇒ 先问 Mod 自己的设置文件；那里也没有才退回"模型内置材质"。
			?? ModSettingsFile.TryRead(modDirectory)?.EnabledMaterialPatches
			?? BuiltInMaterialPatches(report);
		var materials = new List<string>();
		foreach (var name in enabled.Distinct(StringComparer.OrdinalIgnoreCase))
		{
			if (string.Equals(name, unitFileName, StringComparison.OrdinalIgnoreCase)) continue;
			var path = Path.Combine(payload, name);
			if (!File.Exists(path))
			{
				// 跳过一个文件就否掉整次，只会让用户卡住：其余十一个材质都是好的，
				// 而他却看不到任何东西、还被告知"去解决"。跳过、点名、继续。
				warnings.Add($"跳过了 {name}：它已不在基底目录里（可能被删除或改名）。");
				continue;
			}
			materials.Add(path);
		}

		return MaterializeCore(modDirectory, unitLayer, materials, marker, warnings);
	}

	/// <summary>
	/// 宿主**自己那次转换**产出的材质 patch 文件名——它们在选项表里的名字是「模型内置材质」。
	/// 转换报告里逐条写着 <c>materialPatch</c>；报告缺失时为空（此时默认不启用任何材质层）。
	/// </summary>
	private static IReadOnlyList<string> BuiltInMaterialPatches(VpkConversionReport? report)
	{
		if (report is null) return Array.Empty<string>();
		return report.Patches
			.Select(patch => patch.MaterialPatch)
			.Where(name => !string.IsNullOrWhiteSpace(name))
			.Select(name => name!)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}

	private static OverwriteMaterializationResult MaterializeCore(
		string modDirectory,
		string unitLayer,
		IReadOnlyList<string> materialLayers,
		string marker,
		List<string> warnings)
	{
		var overwrite = ModNodePayloadLayout.GetOverwriteDirectory(modDirectory);
		string? backup;
		try
		{
			backup = ManagedOverwriteDirectory.Prepare(overwrite);
		}
		catch (InvalidOperationException exception)
		{
			// 不是自己生成的目录：**不碰**，把原因原样说出去。
			return OverwriteMaterializationResult.Failure(exception.Message, warnings);
		}

		try
		{
			var written = new List<string>();
			written.AddRange(OverwritePatchInstaller.Install(overwrite, unitLayer).WrittenFiles);
			foreach (var material in materialLayers)
				written.AddRange(OverwritePatchInstaller.Install(overwrite, material).WrittenFiles);
			ManagedOverwriteDirectory.Commit(overwrite, marker);
			ManagedOverwriteDirectory.DiscardBackup(backup);
			return new OverwriteMaterializationResult(true, written, warnings, null);
		}
		catch (Exception exception) when (exception is IOException
			or UnauthorizedAccessException
			or System.Security.SecurityException)
		{
			// 失败时把上一次的 Overwrite 原样还回去：用户看到的外观不该因为一次失败的切换变空白。
			ManagedOverwriteDirectory.Rollback(overwrite, backup);
			return OverwriteMaterializationResult.Failure(
				$"物化生效层失败：{exception.Message}", warnings);
		}
	}
}
