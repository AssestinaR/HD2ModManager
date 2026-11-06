using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

/// <summary>
/// 设置一个 Mod 启用哪几个材质选项，并立刻重新生成生效层。
/// <para>
/// 折并之后材质选项都在 Mod 自己的基底目录里（每个选项一个 patch）。用户勾几个就部署几个——
/// 多个材质包的材质 id 互相冲突、后加载的覆盖先加载的，那是包作者之间的事，不在这里拦。
/// </para>
/// </summary>
internal static class CliMaterialSelection
{
	internal sealed record Result(
		bool Succeeded,
		string PatchArchiveHex16,
		IReadOnlyList<string> EnabledPatchFileNames,
		IReadOnlyList<string> WrittenFiles,
		IReadOnlyList<string> Notes,
		string? Error);

	public static Result Set(CliWorkspace workspace, CliLibraryState state, string selector, string spec)
	{
		var node = CliProjections.ResolveMod(state.Snapshot.Nodes, selector);
		var libraryRoot = workspace.LibraryRoot
			?? throw new CliUserException("LibraryNotConfigured", "未能确定 Mod 库。");
		var directory = Path.Combine(libraryRoot, node.RelativePath);
		var payload = ModNodePayloadLayout.TryGetExistingVpkPatchDirectory(directory);
		if (payload is null)
			throw new CliUserException(
				"ModHasNoPayload",
				$"{node.Metadata.Name} 没有基底目录（sectional/），没有可选的材质。");

		var enabled = ParseSpecification(spec);
		var writeError = ModSettingsFile.TryWrite(directory, new ModSettingsFile.Document(
			ModSettingsFile.CurrentSchema, enabled));
		if (writeError is not null)
			return new Result(false, SectionalPatchManifestFile.ReadArchiveHex16(payload), enabled,
				Array.Empty<string>(), Array.Empty<string>(),
				$"启用集合写不进去：{writeError}");

		var materialization = OverwriteMaterializer.Materialize(directory, new OverwriteSelection(enabled));
		return new Result(
			materialization.Succeeded,
			SectionalPatchManifestFile.ReadArchiveHex16(payload),
			enabled,
			materialization.WrittenFiles,
			materialization.Warnings,
			materialization.Error);
	}

	/// <summary><c>none</c> = 一个都不启用；其余按逗号分隔的 patch 文件名。</summary>
	private static IReadOnlyList<string> ParseSpecification(string spec)
	{
		if (string.IsNullOrWhiteSpace(spec) || spec.Equals("none", StringComparison.OrdinalIgnoreCase))
			return Array.Empty<string>();
		return spec
			.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}
}
