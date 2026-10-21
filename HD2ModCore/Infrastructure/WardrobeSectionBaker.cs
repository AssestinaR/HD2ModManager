using System.Buffers.Binary;
using System.Globalization;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

/// <summary>一次烘焙的结果。数字是"真正写下去的 dword 数"，不是"打算写的"。</summary>
public sealed record WardrobeSectionBakeResult(
	int DwordsWritten,
	int SectionsDisabled,
	int SectionsEnabled,
	IReadOnlyList<string> Warnings)
{
	public bool Changed => DwordsWritten != 0;

	public string ToSummary()
		=> Changed
			? $"已烘焙：关掉 {SectionsDisabled} 个 section、打开 {SectionsEnabled} 个，共写 {DwordsWritten} 个 dword。"
			: "已经是目标状态，没有写入任何字节。";
}

/// <summary>
/// 把"哪些 section 要隐藏"直接<b>烘进 patch 里的 unit</b>——也就是在 section 记录的
/// <c>numIndices</c> 字段上写 <c>0</c>（隐藏）或写回原值（显示）。
/// <para>
/// <b>为什么不再走运行时 Lua。</b>游戏 2026-10 那次大改之后，运行时改 <c>numIndices</c> 的那条路
/// 暂时不可用，而"某个 section 不画"这件事本身只需要改一个 uint32。所以同样的语义改为
/// 在产物上落地：patch 就是状态，部署下去即生效。
/// </para>
/// <para>
/// <b>偏移是相对谁的。</b>相对<b>该 unit 自己的 TocData</b>，不是相对整个 patch 文件——
/// <c>sections.json</c> 里的 <c>tocRelative</c> 就是这个意思（当年的 Lua 也是拿 unit 的
/// TocData 去套）。所以这里必须先按 TOC 找到每个 unit 的 payload 落点，再在那一小段里写。
/// 把偏移当文件绝对偏移用，写下去的是别人的字段，而结果"看起来仍然像一份正常的 patch"。
/// </para>
/// <para>
/// <b>为什么可逆而不用备份。</b><c>sections.json</c> 里每个 section 的每级 LOD 都记着
/// 它<b>原本的</b> <c>numIndices</c>（<see cref="WardrobeSectionToggle.NumIndices"/>）。
/// 隐藏写 0，显示写回那个值——两边都能从同一份表算出来，不需要复制一份几十 MB 的原始 payload。
/// </para>
/// <para>
/// <b>为什么可以反复调用。</b>写入是幂等的：已经是目标值就跳过。也正因为如此，
/// 这里<b>不校验</b> <c>sections.json</c> 里记的 patch sha256——第一次烘焙之后 sha 必然变，
/// 拿它当闸门会让第二次编辑永远失败。取而代之的是一条更强的检查：
/// <b>每个要写的 dword 当前必须要么是 0、要么是它原本的值</b>。任何别的值都说明这份
/// <c>sections.json</c> 与这份 patch 不是一套（比如重新转换过），此时拒绝写入并报错。
/// </para>
/// </summary>
public static class WardrobeSectionBaker
{
	/// <summary>
	/// 把 <paramref name="disabledByUnit"/> 描述的禁用集合烘进 patch。
	/// 键是 unit 的 FileId（带不带 <c>0x</c>、大小写都接受），值是要隐藏的 section 下标。
	/// </summary>
	public static async Task<WardrobeSectionBakeResult> BakeAsync(
		string patchTocPath,
		WardrobeSectionMapDocument map,
		IReadOnlyDictionary<string, IReadOnlyCollection<int>> disabledByUnit,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(patchTocPath);
		ArgumentNullException.ThrowIfNull(map);
		ArgumentNullException.ThrowIfNull(disabledByUnit);

		var path = Path.GetFullPath(patchTocPath);
		if (!File.Exists(path))
			throw new FileNotFoundException($"找不到 patch 文件：{path}", path);

		var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
		var slices = ScanUnitSlices(bytes, path);
		var result = Bake(bytes, slices, map, disabledByUnit, cancellationToken);
		if (result.DwordsWritten != 0)
			await WriteAtomicAsync(path, bytes, cancellationToken).ConfigureAwait(false);
		return result;
	}

	/// <summary>
	/// 纯核心：在给定的字节缓冲上按 unit 切片写 dword。
	/// <paramref name="unitSlices"/> 是"unit FileId → (payload 起点, 长度)"。
	/// 抽出来是为了让合成数据的测试不必造一份完整的 patch TOC。
	/// </summary>
	internal static WardrobeSectionBakeResult Bake(
		byte[] bytes,
		IReadOnlyDictionary<string, (int Offset, int Size)> unitSlices,
		WardrobeSectionMapDocument map,
		IReadOnlyDictionary<string, IReadOnlyCollection<int>> disabledByUnit,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(bytes);
		ArgumentNullException.ThrowIfNull(unitSlices);
		ArgumentNullException.ThrowIfNull(map);
		ArgumentNullException.ThrowIfNull(disabledByUnit);

		var disabled = NormalizeDisabled(disabledByUnit);
		var warnings = new List<string>();

		// ① 先全量校验再动手。写到一半才发现对不上，留下的是半烘状态，比不写更难查。
		foreach (var unit in map.Units)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!unitSlices.TryGetValue(unit.FileId, out var slice))
			{
				warnings.Add($"patch 里找不到 unit {unit.FileId} 的 payload；它的 section 无法烘焙。");
				continue;
			}

			IReadOnlyCollection<int> off = disabled.TryGetValue(unit.FileId, out var indices) ? indices : Array.Empty<int>();
			foreach (var section in unit.Sections)
			{
				foreach (var toggle in section.Toggles)
				{
					var absolute = ResolveOffset(slice, toggle, unit.FileId, section.Index, bytes.Length);
					var current = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(absolute, sizeof(uint)));
					if (current != 0 && current != toggle.NumIndices)
					{
						throw new InvalidDataException(
							$"sections.json 与这份 patch 对不上：unit {unit.FileId} 第 {section.Index} 个 section 的 "
							+ $"LOD{toggle.Lod} 当前值是 {current}，既不是 0 也不是记录里的 {toggle.NumIndices}。请重新转换。");
					}
				}
			}
		}

		// ② 再写。只写与目标不同的那些，数字如实统计。
		var written = 0;
		var disabledCount = 0;
		var enabledCount = 0;
		foreach (var unit in map.Units)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!unitSlices.TryGetValue(unit.FileId, out var slice))
				continue;

			IReadOnlyCollection<int> off = disabled.TryGetValue(unit.FileId, out var indices) ? indices : Array.Empty<int>();
			foreach (var section in unit.Sections)
			{
				var hide = off.Contains(section.Index);
				if (hide) disabledCount++;
				else enabledCount++;
				foreach (var toggle in section.Toggles)
				{
					var absolute = ResolveOffset(slice, toggle, unit.FileId, section.Index, bytes.Length);
					var target = hide ? 0u : toggle.NumIndices;
					var current = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(absolute, sizeof(uint)));
					if (current == target)
						continue;
					BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(absolute, sizeof(uint)), target);
					written++;
				}
			}
		}

		// ③ 禁用集合里出现了 patch 里没有的 unit/section：不报错，但必须说出来，
		//    否则用户会以为"关掉了"，而实际上那条掩码指向的是另一个 mod 的 unit。
		foreach (var (unitId, indices) in disabled)
		{
			var unit = map.FindUnit(unitId);
			if (unit is null)
			{
				warnings.Add($"禁用集合里的 unit {unitId} 不在这个 patch 里。");
				continue;
			}

			foreach (var index in indices)
			{
				if (unit.Sections.All(section => section.Index != index))
					warnings.Add($"unit {unitId} 没有第 {index} 个 section（共 {unit.Sections.Count} 个）。");
			}
		}

		return new WardrobeSectionBakeResult(written, disabledCount, enabledCount, warnings);
	}

	/// <summary>
	/// 读出当前实际生效的禁用集合（从 patch 字节反推）。用于"patch 才是事实"的核对，
	/// 也用于在没有源文件时重建界面状态。
	/// </summary>
	public static async Task<IReadOnlyDictionary<string, IReadOnlyCollection<int>>> ReadBakedAsync(
		string patchTocPath,
		WardrobeSectionMapDocument map,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(patchTocPath);
		ArgumentNullException.ThrowIfNull(map);
		var path = Path.GetFullPath(patchTocPath);
		var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
		var slices = ScanUnitSlices(bytes, path);
		return ReadBaked(bytes, slices, map);
	}

	internal static IReadOnlyDictionary<string, IReadOnlyCollection<int>> ReadBaked(
		byte[] bytes,
		IReadOnlyDictionary<string, (int Offset, int Size)> unitSlices,
		WardrobeSectionMapDocument map)
	{
		var result = new Dictionary<string, IReadOnlyCollection<int>>(StringComparer.Ordinal);
		foreach (var unit in map.Units)
		{
			if (!unitSlices.TryGetValue(unit.FileId, out var slice))
				continue;
			var disabled = new List<int>();
			foreach (var section in unit.Sections)
			{
				// 以 LOD0 的落点为准：各级 LOD 是同一个 section 的重复记录，
				// 只要有一级被清零，这一块在这个距离档位上就不画了。
				var toggle = section.Toggles.FirstOrDefault(candidate => candidate.Lod == 0) ?? section.Toggles.FirstOrDefault();
				if (toggle is null)
					continue;
				var absolute = ResolveOffset(slice, toggle, unit.FileId, section.Index, bytes.Length);
				if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(absolute, sizeof(uint))) == 0
					&& toggle.NumIndices != 0)
				{
					disabled.Add(section.Index);
				}
			}

			if (disabled.Count != 0)
				result[unit.FileId] = disabled.Order().ToArray();
		}

		return result;
	}

	/// <summary>把 patch 里每个 Unit 条目的 payload 落点列出来（FileId → 文件内偏移 + 长度）。</summary>
	internal static IReadOnlyDictionary<string, (int Offset, int Size)> ScanUnitSlices(byte[] bytes, string patchName)
	{
		var entries = new PatchTocScanner().ScanEntries(bytes, patchName);
		var slices = new Dictionary<string, (int Offset, int Size)>(StringComparer.Ordinal);
		foreach (var entry in entries)
		{
			if (entry.AssetKey.TypeId != PatchUnitMeshReader.UnitTypeId)
				continue;
			var fileId = entry.AssetKey.FileId.ToString("x16", CultureInfo.InvariantCulture);
			slices[fileId] = (checked((int)entry.TocDataOffset), checked((int)entry.TocDataSize));
		}

		return slices;
	}

	/// <summary>把"相对该 unit payload 的偏移"换算成文件内绝对偏移，并做边界检查。</summary>
	private static int ResolveOffset(
		(int Offset, int Size) slice,
		WardrobeSectionToggle toggle,
		string unitId,
		int sectionIndex,
		int fileLength)
	{
		var relative = ParseOffset(toggle.NumIndicesOffset, unitId, sectionIndex);
		if (relative + sizeof(uint) > slice.Size)
		{
			throw new InvalidDataException(
				$"sections.json 与这份 patch 对不上：unit {unitId} 第 {sectionIndex} 个 section 的 "
				+ $"LOD{toggle.Lod} 落点 0x{relative:x} 超出了该 unit 的 payload（{slice.Size} 字节）。请重新转换。");
		}

		var absolute = slice.Offset + relative;
		if (absolute + sizeof(uint) > fileLength)
		{
			throw new InvalidDataException(
				$"unit {unitId} 的 payload 落点超出了 patch 长度 {fileLength}。请重新转换。");
		}

		return absolute;
	}

	private static Dictionary<string, HashSet<int>> NormalizeDisabled(
		IReadOnlyDictionary<string, IReadOnlyCollection<int>> disabledByUnit)
	{
		var result = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
		foreach (var (rawUnit, indices) in disabledByUnit)
		{
			if (string.IsNullOrWhiteSpace(rawUnit))
				continue;
			var unit = WardrobeSectionFingerprint.TryNormalizeUnit(rawUnit, out var normalized)
				? normalized
				: rawUnit.Trim().ToLowerInvariant();
			if (!result.TryGetValue(unit, out var set))
			{
				set = new HashSet<int>();
				result.Add(unit, set);
			}

			foreach (var index in indices)
			{
				if (index >= 0) set.Add(index);
			}
		}

		return result;
	}

	private static int ParseOffset(string raw, string unit, int sectionIndex)
	{
		var text = raw.Trim();
		if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
			text = text[2..];
		if (!int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var offset) || offset < 0)
		{
			throw new InvalidDataException(
				$"sections.json 里 unit {unit} 第 {sectionIndex} 个 section 的偏移 '{raw}' 不是合法的十六进制。");
		}

		return offset;
	}

	/// <summary>
	/// 原子写回。库里的文件是只读的（导入时统一置只读），所以要先摘掉属性再写、
	/// 写完再挂回去——否则下一次导入/对账会看到一堆属性不一致的文件。
	/// </summary>
	private static async Task WriteAtomicAsync(string path, byte[] bytes, CancellationToken cancellationToken)
	{
		var hadReadOnly = false;
		try
		{
			var attributes = File.GetAttributes(path);
			hadReadOnly = (attributes & FileAttributes.ReadOnly) != 0;
			if (hadReadOnly) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
		}
		catch (IOException) { }
		catch (UnauthorizedAccessException) { }

		var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
			File.Move(temporary, path, overwrite: true);
		}
		finally
		{
			try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { }
			if (hadReadOnly && File.Exists(path))
			{
				try { File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly); }
				catch (IOException) { }
				catch (UnauthorizedAccessException) { }
			}
		}
	}
}
