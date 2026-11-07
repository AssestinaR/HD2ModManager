using HD2ModCore.Application;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// 把老库里还叫 <c>vpk2patch/</c> 的基底目录改名为 <c>sectional/</c>。
/// <para>
/// <b>为什么需要它。</b>2026-11 把基底目录改名了。读路径确实同时认两个名字
/// （见 <see cref="ModNodePayloadLayout.TryGetExistingVpkPatchDirectory"/>），但"两个名字都认"
/// 只是过渡期的保险，不该成为长期形态：只要库里有旧名目录，写路径就必须不断回答
/// "这次写进哪一个"，而任何一个回答错的调用点都会在同一个 Mod 里造出两层基底——
/// 旧层里被删掉的 patch 会以"只在旧层存在"的身份复活。
/// </para>
/// <para>
/// <b>为什么只是一次性改名，不做别的。</b>目录里的文件一个字节都不用动：它们记的是
/// patch 内容，与目录叫什么无关。<c>serialize → 改名</c> 之外任何"顺手整理"都是多余风险。
/// </para>
/// <para>
/// <b>新旧同时存在时不动。</b>那说明有人已经在新名下写过东西，两边都可能是"当前"。
/// 猜错会静默改变用户的观感，所以只报告、不合并——交给显式修复命令或用户决定。
/// </para>
/// </summary>
public sealed class ModContentLayoutMigration : IModContentLayoutMigration
{
	public ValueTask<ModContentLayoutMigrationResult> MigrateAsync(
		string modsRootDirectory,
		LibrarySnapshot? snapshot = null,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(modsRootDirectory);
		var root = Path.GetFullPath(modsRootDirectory);
		if (!Directory.Exists(root)) return ValueTask.FromResult(ModContentLayoutMigrationResult.Nothing);

		var renamed = 0;
		var alreadyCurrent = 0;
		var adopted = 0;
		var manifests = 0;
		var conflicts = new List<string>();
		var failures = new List<string>();
		// 老库把名字/备注只存在 library.json 里。文件夹是权威之后，先把它们落到各自的
		// `.hd2modmanager-mod.json`，否则"把文件夹搬到另一个库"就会丢名字——那正是要解决的事。
		var nodesByPath = snapshot?.Nodes.Values
			.GroupBy(node => node.RelativePath, StringComparer.OrdinalIgnoreCase)
			.ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

		foreach (var node in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
		{
			cancellationToken.ThrowIfCancellationRequested();
			var name = Path.GetFileName(node);
			// 管理器自己的目录（护甲映射工作区、换载荷备份、回收站…）不参与。
			if (name.StartsWith(CustomModelCatalog.ManagerDirectoryPrefix, StringComparison.OrdinalIgnoreCase))
				continue;

			if (nodesByPath is not null && nodesByPath.TryGetValue(name, out var nodeRecord)
				&& AdoptSettingsFile(node, nodeRecord))
				adopted++;

			// 结构文件也补一份：`patches.json` 是"每个 patch 组是什么"的唯一来源，
			// 而它只在转换/交换/折并时被写。老库里的 Mod 从来没有过它，
			// 于是 `mod show` 的材料选项表是空的、`mod material set` 无从下手。
			if (BackfillManifest(node))
				manifests++;

			var legacy = Path.Combine(node, ModNodePayloadLayout.LegacyVpkPatchDirectoryName);
			var current = Path.Combine(node, ModNodePayloadLayout.VpkPatchDirectoryName);
			var hasLegacy = Directory.Exists(legacy);
			var hasCurrent = Directory.Exists(current);

			if (!hasLegacy)
			{
				alreadyCurrent++;
				continue;
			}
			if (hasCurrent)
			{
				conflicts.Add(name);
				continue;
			}

			try
			{
				Directory.Move(legacy, current);
				renamed++;
			}
			catch (Exception exception) when (exception is IOException
				or UnauthorizedAccessException
				or System.Security.SecurityException)
			{
				failures.Add($"{name}: {exception.Message}");
			}
		}

		return ValueTask.FromResult(new ModContentLayoutMigrationResult(
			renamed, alreadyCurrent, conflicts, failures, adopted, manifests));
	}

	/// <summary>
	/// 把一个 Mod 的名字/备注/id 写进它自己的设置文件。已有完整内容的不动（幂等）。
	/// </summary>
	/// <summary>
	/// 给"有基底目录但没有结构文件"的 Mod 补一份 <c>patches.json</c>。
	/// 已有就一个字都不动——它是派生数据，但用户可能已经在界面里调整过选项。
	/// </summary>
	private static bool BackfillManifest(string modDirectory)
	{
		var payload = ModNodePayloadLayout.TryGetExistingVpkPatchDirectory(modDirectory);
		if (payload is null) return false;
		if (File.Exists(SectionalPatchManifestFile.GetPath(payload))) return false;

		var archive = SectionalPatchManifestFile.ReadArchiveHex16(payload);
		if (string.IsNullOrEmpty(archive)) return false;

		var document = SectionalPatchManifestFile.Build(
			payload, archive, VpkConversionReport.TryRead(modDirectory), DateTimeOffset.UtcNow);
		return SectionalPatchManifestFile.TryWrite(payload, document) is null;
	}

	private static bool AdoptSettingsFile(string modDirectory, ModNode node)
	{
		var existing = ModSettingsFile.TryRead(modDirectory);
		var id = node.Id.Value.ToString("N");
		if (existing is not null
			&& string.Equals(existing.Id, id, StringComparison.OrdinalIgnoreCase)
			&& string.Equals(existing.Name, node.Metadata.Name, StringComparison.Ordinal)
			&& string.Equals(existing.Notes, node.Metadata.Notes, StringComparison.Ordinal))
			return false;

		var document = new ModSettingsFile.Document(
			ModSettingsFile.CurrentSchema,
			existing?.EnabledMaterialPatches,
			id,
			node.Metadata.Name,
			node.Metadata.Notes);
		return ModSettingsFile.TryWrite(modDirectory, document) is null;
	}
}
