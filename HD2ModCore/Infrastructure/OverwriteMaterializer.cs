using HD2ModCore.Decoration;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// 用户对外观做的一次选择。<b>现在是参数、还没有落盘</b>——持久化会随 `mod.json` 一起做（P4）。
/// </summary>
/// <param name="MaterialPatchFileName">
/// 选中的材质 patch 文件名（例如 <c>9ba626afa44a3aa3.patch_2</c>）；<c>null</c> = 不加材质层。
/// 槽位是**单选**：变体之间共用同一套材质 id，同时部署两份只会让后加载的那份覆盖前者。
/// </param>
public sealed record OverwriteSelection(string? MaterialPatchFileName)
{
	public static OverwriteSelection None { get; } = new((string?)null);
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
/// <b>组装规则。</b><c>Overwrite/</c> = unit patch（<c>patch_0</c>，或被换甲替换过的那份）
/// ＋**选中的那一份**材质 patch。没被选中的材质 patch 留在基底目录里，不进来、也就不部署。
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

		var unitLayer = unitLayerPatchPath;
		if (string.IsNullOrWhiteSpace(unitLayer))
		{
			var archive = SectionalPatchManifestFile.ReadArchiveHex16(payload);
			if (string.IsNullOrEmpty(archive))
				return OverwriteMaterializationResult.Failure(
					"基底目录里没有 unit patch，无法物化生效层。");
			unitLayer = Path.Combine(payload, $"{archive}.patch_{UnitPatchIndex}");
		}
		if (!File.Exists(unitLayer))
			return OverwriteMaterializationResult.Failure($"unit 层 patch 不存在：{unitLayer}");

		string? materialLayer = null;
		if (!string.IsNullOrWhiteSpace(selection.MaterialPatchFileName))
		{
			materialLayer = Path.Combine(payload, selection.MaterialPatchFileName);
			if (!File.Exists(materialLayer))
				return OverwriteMaterializationResult.Failure(
					$"选中的材质 patch 不在基底目录里：{selection.MaterialPatchFileName}。"
					+ "它可能已被删除或改名；重新选一次即可。");
			// 选中的就是 unit 层本身：没有意义，但也不是错误——按"不加材质层"处理并说出来。
			if (string.Equals(
				Path.GetFileName(materialLayer),
				Path.GetFileName(unitLayer),
				StringComparison.OrdinalIgnoreCase))
				return MaterializeCore(modDirectory, unitLayer, null, marker,
					[$"选中的材质 patch 与 unit 层是同一份（{selection.MaterialPatchFileName}），已按“不加材质层”处理。"]);
		}

		return MaterializeCore(modDirectory, unitLayer, materialLayer, marker, []);
	}

	private static OverwriteMaterializationResult MaterializeCore(
		string modDirectory,
		string unitLayer,
		string? materialLayer,
		string marker,
		IReadOnlyList<string> warnings)
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
			if (materialLayer is not null)
				written.AddRange(OverwritePatchInstaller.Install(overwrite, materialLayer).WrittenFiles);
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
