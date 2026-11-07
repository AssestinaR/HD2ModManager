using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

/// <summary>材质选项的一行：详情页那块列表、命令行的输出都用它。</summary>
public sealed record MaterialOptionView(
	int PatchIndex,
	string PatchFileName,
	string? Name,
	bool Enabled,
	int Order,
	string? Notes,
	string? ImageFileName);

/// <summary>
/// 改动一个 Mod 的材质选项：启用/停用、排序、改名、备注、配图。
/// <para>
/// <b>只改两个文件，从不碰 patch。</b>结构文件（<c>patches.json</c>）装顺序、名字、备注、配图名；
/// Mod 自己的设置文件（<c>.hd2modmanager-mod.json</c>）装"启用了哪几个"。
/// 部署时按结构文件里的顺序排序，于是"谁覆盖谁"由列表顺序决定。
/// </para>
/// <para>
/// 这些选项**不是**库里的独立 Mod：它们是主 Mod 的组成部分，所以这里没有"删除"和"移出列表"——
/// 一个材质 patch 只要还在基底目录里，就永远在列表里。
/// </para>
/// </summary>
public static class MaterialOptionSettings
{
	/// <summary>选项配图存放目录（在 Mod 自己的管理器目录下，不污染载荷目录）。</summary>
	public static string GetOptionsDirectory(string modDirectory)
		=> Path.Combine(modDirectory, CustomModelCatalog.ManagerDirectoryPrefix, "options");

	public static IReadOnlyList<MaterialOptionView> Read(string modDirectory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(modDirectory);
		var payload = ModNodePayloadLayout.TryGetExistingVpkPatchDirectory(modDirectory);
		if (payload is null) return Array.Empty<MaterialOptionView>();

		var manifest = SectionalPatchManifestFile.TryReadAsync(payload).AsTask().GetAwaiter().GetResult();
		if (manifest is null) return Array.Empty<MaterialOptionView>();

		var enabled = ModSettingsFile.TryRead(modDirectory)?.EnabledMaterialPatches;
		var options = new List<MaterialOptionView>();
		foreach (var entry in manifest.Patches.Where(entry => entry.Role == SectionalPatchRole.Material))
		{
			var fileName = $"{entry.ArchiveHex16}.patch_{entry.PatchIndex}";
			options.Add(new MaterialOptionView(
				entry.PatchIndex,
				fileName,
				entry.OptionName,
				// 没有显式记录时按"模型内置材质默认启用"算——与物化器的默认一致。
				enabled is null
					? string.Equals(entry.OptionName, SectionalPatchManifest.BuiltInMaterialOptionName, StringComparison.Ordinal)
					: enabled.Contains(fileName, StringComparer.OrdinalIgnoreCase),
				entry.Order ?? entry.PatchIndex,
				entry.Notes,
				entry.ImageFileName));
		}
		// 展示顺序 = 部署顺序（小的在前，后部署的覆盖先部署的）。
		return options.OrderBy(option => option.Order).ThenBy(option => option.PatchIndex).ToArray();
	}

	/// <summary>把一个选项挪到新位置（0 起）。返回 <c>null</c> 表示写成功。</summary>
	public static string? Move(string modDirectory, IReadOnlyList<int> orderedPatchIndexes)
	{
		ArgumentNullException.ThrowIfNull(orderedPatchIndexes);
		return Mutate(modDirectory, entries =>
		{
			var rank = orderedPatchIndexes
				.Select((index, position) => (index, position))
				.ToDictionary(pair => pair.index, pair => pair.position);
			for (var i = 0; i < entries.Count; i++)
			{
				var current = entries[i];
				// 没有出现在给定顺序里的条目排在后面，保持它们原来的相对次序。
				var order = rank.TryGetValue(current.PatchIndex, out var position)
					? position
					: orderedPatchIndexes.Count + current.PatchIndex;
				entries[i] = current with { Order = order };
			}
		});
	}

	public static string? SetName(string modDirectory, int patchIndex, string? name)
		=> Mutate(modDirectory, entries => Replace(entries, patchIndex, entry => entry with
		{
			OptionName = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
		}));

	public static string? SetNotes(string modDirectory, int patchIndex, string? notes)
		=> Mutate(modDirectory, entries => Replace(entries, patchIndex, entry => entry with
		{
			Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
		}));

	/// <summary>换配图。<paramref name="sourceImagePath"/> 为 <c>null</c> 表示清掉。</summary>
	public static string? SetImage(string modDirectory, int patchIndex, string? sourceImagePath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(modDirectory);
		if (sourceImagePath is null)
			return Mutate(modDirectory, entries => Replace(entries, patchIndex, entry => entry with { ImageFileName = null }));
		if (!File.Exists(sourceImagePath)) return $"配图不存在：{sourceImagePath}";

		var directory = GetOptionsDirectory(modDirectory);
		var fileName = $"{Path.GetFileNameWithoutExtension(sourceImagePath)}-{patchIndex}{Path.GetExtension(sourceImagePath)}";
		try
		{
			Directory.CreateDirectory(directory);
			File.Copy(sourceImagePath, Path.Combine(directory, fileName), overwrite: true);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return $"配图复制失败：{exception.Message}";
		}
		return Mutate(modDirectory, entries => Replace(entries, patchIndex, entry => entry with { ImageFileName = fileName }));
	}

	/// <summary>启用哪几个（多选，不限制数量）。</summary>
	public static string? SetEnabled(string modDirectory, IReadOnlyList<string> enabledPatchFileNames)
	{
		// 只读一次：这份文件里还有 id/名字/备注，重写时必须原样带上，否则一次勾选就把它们抹了。
		var existing = ModSettingsFile.TryRead(modDirectory);
		return ModSettingsFile.TryWrite(modDirectory, new ModSettingsFile.Document(
			ModSettingsFile.CurrentSchema,
			enabledPatchFileNames,
			existing?.Id,
			existing?.Name,
			existing?.Notes));
	}

	private static void Replace(
		List<SectionalPatchGroupEntry> source,
		int patchIndex,
		Func<SectionalPatchGroupEntry, SectionalPatchGroupEntry> update)
	{
		for (var i = 0; i < source.Count; i++)
		{
			if (source[i].PatchIndex != patchIndex) continue;
			source[i] = update(source[i]);
			return;
		}
	}

	/// <summary>读—改—写结构文件。只动用户字段，派生的那些原样留着。</summary>
	private static string? Mutate(
		string modDirectory,
		Action<List<SectionalPatchGroupEntry>> mutate)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(modDirectory);
		var payload = ModNodePayloadLayout.TryGetExistingVpkPatchDirectory(modDirectory);
		if (payload is null) return "这个 Mod 没有基底目录（sectional/），没有材质选项。";

		var document = SectionalPatchManifestFile.TryReadAsync(payload).AsTask().GetAwaiter().GetResult();
		if (document is null) return "这个 Mod 还没有结构文件（patches.json），读不到材质选项。";

		var entries = new List<SectionalPatchGroupEntry>(document.Patches);
		mutate(entries);
		var updated = document with
		{
			GeneratedUtc = DateTimeOffset.UtcNow,
			Patches = entries,
		};
		return SectionalPatchManifestFile.TryWrite(payload, updated);
	}
}
