using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// 计算「把材质 Option 折进宿主」的只读计划。
/// <para>
/// <b>折并要解决的问题。</b>今天颜色变体是**兄弟 Option mod**：一个宿主 + N 个材质包。
/// 于是"两套外观同时在场"做不到（同一套 unit 上只能有一个材质赢），而且把宿主从一个库
/// 搬到另一个库时必须记得带上整族——少一个就白模。折并之后，材质 patch 都平铺在宿主自己的
/// 基底目录里（<c>patch_1..patch_N</c>），宿主成为一个自洽单元：复制文件夹就行。
/// </para>
/// <para>
/// <b>为什么只算不做。</b>见 <see cref="MaterialFoldPlan"/> 的说明——这是会移动用户文件的
/// 迁移，"猜错搬哪一个"不可逆。
/// </para>
/// </summary>
public static class MaterialOptionFoldPlanner
{
	public static MaterialFoldPlan Plan(
		string hostPayloadDirectory,
		IReadOnlyList<MaterialFoldMember> members)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(hostPayloadDirectory);
		ArgumentNullException.ThrowIfNull(members);

		var blockers = new List<MaterialFoldBlocker>();
		var skipped = new List<string>();
		var entries = new List<MaterialFoldEntry>();
		var archive = SectionalPatchManifestFile.ReadArchiveHex16(hostPayloadDirectory);
		var existing = ReadExistingPatchIndices(hostPayloadDirectory);

		if (string.IsNullOrEmpty(archive) || !existing.Contains(0))
		{
			blockers.Add(new MaterialFoldBlocker(
				MaterialFoldBlockerKind.HostHasNoUnitPatch,
				null,
				$"宿主的基底目录里没有 unit patch（{SectionalPatchManifestFile.GetPath(hostPayloadDirectory)} 同级应当有 <archive>.patch_0）。"));
			return new MaterialFoldPlan(hostPayloadDirectory, archive, entries, blockers, skipped);
		}

		var nextIndex = existing.Count == 0 ? 1 : existing.Max() + 1;

		foreach (var member in members.OrderBy(item => item.Order).ThenBy(item => item.ModName, StringComparer.OrdinalIgnoreCase))
		{
			var patches = ReadPatchFiles(member.PayloadDirectory);
			if (patches.Count == 0)
			{
				skipped.Add($"{member.ModName}：基底目录里没有 patch，已跳过（它可能不是材质包）。");
				continue;
			}
			if (patches.Count > 1)
			{
				skipped.Add($"{member.ModName}：基底目录里有 {patches.Count} 份 patch，无法判断该搬哪一份，已跳过。");
				continue;
			}

			var index = nextIndex++;
			entries.Add(new MaterialFoldEntry(
				member.NodeId,
				member.ModName,
				patches[0],
				Directory.GetParent(member.PayloadDirectory)?.FullName ?? member.PayloadDirectory,
				$"{archive}.patch_{index}",
				index,
				member.Enabled,
				member.Order));
		}

		return new MaterialFoldPlan(hostPayloadDirectory, archive, entries, blockers, skipped);
	}

	/// <summary>宿主基底目录里已经用掉的 patch 序号。</summary>
	private static HashSet<int> ReadExistingPatchIndices(string payloadDirectory)
	{
		var indices = new HashSet<int>();
		if (!Directory.Exists(payloadDirectory)) return indices;
		foreach (var path in Directory.EnumerateFiles(payloadDirectory, "*.patch_*", SearchOption.TopDirectoryOnly))
		{
			var name = Path.GetFileName(path);
			if (name.EndsWith(".gpu_resources", StringComparison.OrdinalIgnoreCase)
				|| name.EndsWith(".stream", StringComparison.OrdinalIgnoreCase))
				continue;
			var marker = name.LastIndexOf(".patch_", StringComparison.OrdinalIgnoreCase);
			if (marker < 0) continue;
			if (int.TryParse(
				name.AsSpan(marker + ".patch_".Length),
				System.Globalization.NumberStyles.None,
				System.Globalization.CultureInfo.InvariantCulture,
				out var index))
				indices.Add(index);
		}
		return indices;
	}

	/// <summary>一个基底目录里的 patch 文件（不含 sidecar）。</summary>
	private static IReadOnlyList<string> ReadPatchFiles(string payloadDirectory)
	{
		if (string.IsNullOrWhiteSpace(payloadDirectory) || !Directory.Exists(payloadDirectory))
			return Array.Empty<string>();
		var patches = new List<string>();
		foreach (var path in Directory.EnumerateFiles(payloadDirectory, "*.patch_*", SearchOption.TopDirectoryOnly))
		{
			var name = Path.GetFileName(path);
			if (name.EndsWith(".gpu_resources", StringComparison.OrdinalIgnoreCase)
				|| name.EndsWith(".stream", StringComparison.OrdinalIgnoreCase))
				continue;
			patches.Add(path);
		}
		patches.Sort(StringComparer.OrdinalIgnoreCase);
		return patches;
	}
}
