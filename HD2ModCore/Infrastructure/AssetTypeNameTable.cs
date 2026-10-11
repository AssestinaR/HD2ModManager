using System.Globalization;
using System.Reflection;

namespace HD2ModCore.Infrastructure;

// 作用：把游戏资产类型哈希翻译成可读名称（unit / state_machine / cloth ...），并支持反向解析。
//       数据来源与管理器设置页使用的那份 typehash.txt 完全同源（同一文件以内嵌资源方式共享），
//       因此不存在第二份需要同步维护的真相。CLI 在没有管理器安装的机器上也能正常显示类型名。
// Purpose: Translates game asset type hashes to readable names (unit / state_machine / cloth ...) and back.
//          The data is the very same typehash.txt the settings page uses, shared as an embedded resource,
//          so there is no second copy to keep in sync. The CLI therefore still shows type names on a
//          machine that has no manager installation.
public static class AssetTypeNameTable
{
	private const string ResourceSuffix = "typehash.txt";

	private static readonly Lazy<IReadOnlyDictionary<ulong, string>> NamesById = new(Load);
	private static readonly Lazy<IReadOnlyDictionary<string, ulong>> IdsByName = new(CreateReverseIndex);

	/// <summary>人类可读的类型名；未知类型回退为 0x 十六进制，绝不返回 null。</summary>
	public static string Resolve(ulong typeId)
		=> NamesById.Value.TryGetValue(typeId, out var name) ? name : $"0x{typeId:x16}";

	public static bool TryGetName(ulong typeId, out string name)
		=> NamesById.Value.TryGetValue(typeId, out name!);

	/// <summary>按十六进制哈希或类型名解析；两者都接受，便于人和脚本各取所需。</summary>
	public static bool TryResolve(string? value, out ulong typeId)
	{
		typeId = 0;
		if (string.IsNullOrWhiteSpace(value)) return false;

		var trimmed = value.Trim();
		if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
		{
			return ulong.TryParse(trimmed.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out typeId);
		}

		if (ulong.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var decimalId))
		{
			typeId = decimalId;
			return true;
		}

		return IdsByName.Value.TryGetValue(trimmed, out typeId);
	}

	public static IReadOnlyDictionary<ulong, string> All => NamesById.Value;

	private static IReadOnlyDictionary<string, ulong> CreateReverseIndex()
	{
		var result = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
		foreach (var (id, name) in NamesById.Value)
		{
			// 同一类型名只保留一个哈希；表里出现过重复行（strings 出现了两次），取首次即可。
			result.TryAdd(name, id);
		}

		return result;
	}

	private static IReadOnlyDictionary<ulong, string> Load()
	{
		var assembly = typeof(AssetTypeNameTable).Assembly;
		var name = assembly.GetManifestResourceNames()
			.SingleOrDefault(candidate => candidate.EndsWith(ResourceSuffix, StringComparison.Ordinal));
		if (name is null)
			throw new InvalidOperationException($"内置资产类型哈希表缺失：{ResourceSuffix}");

		using var stream = assembly.GetManifestResourceStream(name)
			?? throw new InvalidOperationException($"无法读取内置资产类型哈希表：{ResourceSuffix}");
		using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

		var result = new Dictionary<ulong, string>();
		while (reader.ReadLine() is { } line)
		{
			var trimmed = line.Trim();
			if (trimmed.Length == 0) continue;
			var separator = trimmed.IndexOfAny([' ', '\t']);
			if (separator <= 0) continue;
			if (!ulong.TryParse(trimmed.AsSpan(0, separator), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var typeId))
				continue;
			var typeName = trimmed[(separator + 1)..].Trim();
			if (typeName.Length == 0) continue;
			result[typeId] = typeName;
		}

		return result;
	}
}
