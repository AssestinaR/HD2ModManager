using System.Globalization;
using System.Text;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// <c>wardrobe.masks</c> 的行格式读写。规格见
/// <c>HD2RuntimeArmorOverride/notes/wardrobe-schema-v2-20261017.md</c>。
///
/// 这是**编译产物**，不是权威数据：权威数据在 mod 库内的未编译源文件里，由管理器
/// 编辑即保存。这份文件丢了没关系，管理器重新应用一次就会再生成。
/// 因此它只保留运行时需要的东西——没有继承关系、没有编辑器结构。
///
/// 为什么掩码不写进 <c>armor-registry.protocol</c>：协议有 64 KiB 上限，而且有
/// 三份独立解析器；掩码在满配情况下可达上百 KiB，塞进去会把三份实现同时顶到墙上。
///
/// This is a compiled artefact, not the authoritative store: that lives in the mod
/// library's uncompiled source and is saved by the manager on every edit. Losing this
/// file costs one re-apply. It therefore carries only what the runtime needs.
/// </summary>
public static class WardrobeMaskFile
{
	/// <summary>注册表目录下的文件名。</summary>
	public const string FileName = "wardrobe.masks";

	/// <summary>单列上限。协议是 64 KiB，掩码宽松十六倍仍远小于一次读盘的成本。</summary>
	public const int MaximumBytes = 1024 * 1024;

	private const int MaximumSectionIndex = 65535;
	private const int MaximumRevision = int.MaxValue;
	private const uint MaximumSetId = 0xFFFF;

	/// <summary>解析失败时抛出，消息里一定带行号——否则用户面对一份几千行的文件无从下手。</summary>
	public static WardrobeMaskDocument Parse(string text)
	{
		ArgumentNullException.ThrowIfNull(text);
		if (Encoding.UTF8.GetByteCount(text) > MaximumBytes)
			throw new InvalidDataException($"wardrobe.masks 超过 {MaximumBytes} 字节上限。");

		int? schema = null;
		int? revision = null;
		bool? enabled = null;
		var entries = new List<WardrobeMaskEntry>();
		var targetSets = new Dictionary<uint, uint>();
		var entrySeen = new HashSet<(uint, string, int, string)>();
		var lineNumber = 0;
		foreach (var rawLine in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
		{
			lineNumber++;
			var line = rawLine.Trim();
			if (line.Length == 0 || line.StartsWith('#'))
				continue;
			var separator = line.IndexOf('=');
			if (separator <= 0)
				throw Invalid(lineNumber, "缺少 key=value 结构。");
			var key = line[..separator].Trim();
			var value = line[(separator + 1)..].Trim();
			switch (key)
			{
				case "schema":
					if (schema is not null)
						throw Invalid(lineNumber, "schema 重复。");
					schema = ParseInteger(value, lineNumber, "schema", 2, 2);
					break;
				case "revision":
					if (revision is not null)
						throw Invalid(lineNumber, "revision 重复。");
					revision = ParseInteger(value, lineNumber, "revision", 0, MaximumRevision);
					break;
				case "enabled":
					if (enabled is not null)
						throw Invalid(lineNumber, "enabled 重复。");
					enabled = value switch
					{
						"true" => true,
						"false" => false,
						_ => throw Invalid(lineNumber, "enabled 必须是 true 或 false。"),
					};
					break;
				case "off":
					{
						var parts = Split(value);
						if (parts.Length != 4)
							throw Invalid(lineNumber, "off 必须是 setId,unit,sectionIndex,fingerprint。");
						var setId = ParseInteger(parts[0], lineNumber, "掩码集 ID", 0, (int)MaximumSetId);
						var unit = ParseUnit(parts[1], lineNumber);
						var index = ParseInteger(parts[2], lineNumber, "section 下标", 0, MaximumSectionIndex);
						var fingerprint = ParseFingerprint(parts[3], lineNumber);
						if (!entrySeen.Add(((uint)setId, unit, index, fingerprint)))
							throw Invalid(lineNumber, "重复的掩码条目。");
						entries.Add(new WardrobeMaskEntry((uint)setId, unit, index, fingerprint));
						break;
					}
				case "target":
					{
						var parts = Split(value);
						if (parts.Length != 2)
							throw Invalid(lineNumber, "target 必须是 targetItemId,setId。");
						var target = ParseItemId(parts[0], lineNumber);
						var setId = ParseInteger(parts[1], lineNumber, "掩码集 ID", 0, (int)MaximumSetId);
						if (!targetSets.TryAdd(target, (uint)setId))
							throw Invalid(lineNumber, $"目标护甲 {target} 的掩码集重复。");
						break;
					}
				default:
					throw Invalid(lineNumber, $"未知字段：{key}");
			}
		}

		if (schema is null)
			throw new InvalidDataException("wardrobe.masks 必须包含 schema=2。");
		if (revision is null)
			throw new InvalidDataException("wardrobe.masks 缺少 revision。");
		if (enabled is null)
			throw new InvalidDataException("wardrobe.masks 缺少 enabled。");

		return Normalize(new WardrobeMaskDocument(revision.Value, enabled.Value, entries, targetSets));
	}

	/// <summary>
	/// 规范化：排序。序列化也走这里，保证"读进来再写出去"逐字节稳定——
	/// 否则"内容没变"会被误判成"变了"，白白触发一次重载。
	/// </summary>
	public static WardrobeMaskDocument Normalize(WardrobeMaskDocument document)
	{
		ArgumentNullException.ThrowIfNull(document);
		var entries = document.Entries
			.OrderBy(entry => entry.SetId)
			.ThenBy(entry => entry.Unit, StringComparer.Ordinal)
			.ThenBy(entry => entry.SectionIndex)
			.ThenBy(entry => entry.Fingerprint, StringComparer.Ordinal)
			.ToArray();
		var targets = document.TargetSets
			.OrderBy(pair => pair.Key)
			.ToDictionary(pair => pair.Key, pair => pair.Value);
		return new WardrobeMaskDocument(document.Revision, document.Enabled, entries, targets);
	}

	public static string Serialize(WardrobeMaskDocument document)
	{
		var normalized = Normalize(document);
		var builder = new StringBuilder();
		builder.Append("schema=2\n");
		builder.Append("revision=").Append(normalized.Revision.ToString(CultureInfo.InvariantCulture)).Append('\n');
		builder.Append("enabled=").Append(normalized.Enabled ? "true" : "false").Append('\n');
		foreach (var entry in normalized.Entries)
		{
			builder.Append("off=").Append(entry.SetId.ToString(CultureInfo.InvariantCulture)).Append(',')
				.Append(entry.Unit).Append(',')
				.Append(entry.SectionIndex.ToString(CultureInfo.InvariantCulture)).Append(',')
				.Append(entry.Fingerprint).Append('\n');
		}
		foreach (var pair in normalized.TargetSets)
		{
			builder.Append("target=").Append(pair.Key.ToString(CultureInfo.InvariantCulture)).Append(',')
				.Append(pair.Value.ToString(CultureInfo.InvariantCulture)).Append('\n');
		}
		return builder.ToString();
	}

	/// <summary>读文件；不存在时返回 <see cref="WardrobeMaskDocument.Empty"/>（等价于全启用）。</summary>
	public static WardrobeMaskDocument Read(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		if (!File.Exists(path))
			return WardrobeMaskDocument.Empty;
		var info = new FileInfo(path);
		if (info.Length > MaximumBytes)
			throw new InvalidDataException($"wardrobe.masks 超过 {MaximumBytes} 字节上限：{path}");
		return Parse(File.ReadAllText(path, Encoding.UTF8));
	}

	/// <summary>
	/// 原子写：先写同目录临时文件再替换。运行时可能正在读，半写状态会被解析成残缺文档。
	/// </summary>
	public static void Write(string path, WardrobeMaskDocument document)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		var text = Serialize(document);
		if (Encoding.UTF8.GetByteCount(text) > MaximumBytes)
			throw new InvalidDataException($"wardrobe.masks 超过 {MaximumBytes} 字节上限：{path}");
		var directory = Path.GetDirectoryName(Path.GetFullPath(path));
		if (!string.IsNullOrEmpty(directory))
			Directory.CreateDirectory(directory);
		var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			File.WriteAllText(temporaryPath, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			File.Move(temporaryPath, path, overwrite: true);
		}
		catch
		{
			// 失败时不要留下临时文件：它们会被下次扫描当成垃圾，也会让目录变脏。
			if (File.Exists(temporaryPath))
				File.Delete(temporaryPath);
			throw;
		}
	}

	private static string[] Split(string value)
	{
		var parts = value.Split(',');
		for (var index = 0; index < parts.Length; index++)
			parts[index] = parts[index].Trim();
		return parts;
	}

	private static string ParseUnit(string value, int lineNumber)
		=> WardrobeSectionFingerprint.TryNormalizeUnit(value, out var unit)
			? unit
			: throw Invalid(lineNumber, "unit ID 必须是 16 位 hex。");

	private static string ParseFingerprint(string value, int lineNumber)
		=> WardrobeSectionFingerprint.TryNormalizeFingerprint(value, out var fingerprint)
			? fingerprint
			: throw Invalid(lineNumber, "指纹格式无效，应为 <materialId|none>-<triangles>。");

	private static uint ParseItemId(string value, int lineNumber)
		=> uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result)
			? result
			: throw Invalid(lineNumber, "item ID 必须是 uint32。");

	private static int ParseInteger(string value, int lineNumber, string field, int minimum, int maximum)
	{
		if (!int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var result)
			|| result < minimum || result > maximum)
			throw Invalid(lineNumber, $"{field} 必须是 {minimum}..{maximum} 的整数。");
		return result;
	}

	private static InvalidDataException Invalid(int lineNumber, string message)
		=> new($"wardrobe.masks 第 {lineNumber} 行：{message}");
}
