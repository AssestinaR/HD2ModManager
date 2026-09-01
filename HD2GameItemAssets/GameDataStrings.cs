using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace HD2GameItemAssets;

/// <summary>
/// Describes the language identity embedded in a game's <c>strings</c> asset.
/// The language hash is the high 32 bits of the game's Murmur64A hash for the
/// short language key (for example <c>us</c>, <c>cn</c>, or <c>tc</c>).
/// </summary>
public sealed record GameDataLanguageInfo(
	uint Hash,
	string Code,
	string Locale,
	string DisplayName);

/// <summary>Known language keys used by Helldivers 2's strings assets.</summary>
public static class GameDataLanguages
{
	private static readonly IReadOnlyList<(string Code, string Locale, string DisplayName)> Definitions =
	[
		("us", "en-US", "English (US)"),
		("gb", "en-GB", "English (UK)"),
		("bp", "pt-BR", "Português (Brasil)"),
		("de", "de-DE", "Deutsch"),
		("es", "es-ES", "Español (España)"),
		("fr", "fr-FR", "Français"),
		("it", "it-IT", "Italiano"),
		("jp", "ja-JP", "日本語"),
		("ko", "ko-KR", "한국어"),
		("ms", "es-419", "Español (Latinoamérica)"),
		("nl", "nl-NL", "Nederlands"),
		("pl", "pl-PL", "Polski"),
		("pt", "pt-PT", "Português (Portugal)"),
		("ru", "ru-RU", "Русский"),
		// Current GameData uses cn/tc. sc is retained as a compatibility alias
		// for older data builds that used the SDK's simplified-Chinese key.
		("cn", "zh-CN", "简体中文"),
		("sc", "zh-CN", "简体中文"),
		("tc", "zh-TW", "繁體中文"),
	];

	private static readonly IReadOnlyDictionary<uint, GameDataLanguageInfo> ByHash =
		Definitions
			.Select(definition => new GameDataLanguageInfo(
				unchecked((uint)(StingrayHash.Murmur64A(definition.Code) >> 32)),
				definition.Code,
				definition.Locale,
				definition.DisplayName))
			.GroupBy(language => language.Hash)
			.ToDictionary(group => group.Key, group => group.First());

	public static bool TryGet(uint hash, out GameDataLanguageInfo language)
		=> ByHash.TryGetValue(hash, out language!);

	public static GameDataLanguageInfo Resolve(uint hash)
	{
		if (TryGet(hash, out var language)) return language;
		return new GameDataLanguageInfo(
			hash,
			$"hash-{hash.ToString("x8", CultureInfo.InvariantCulture)}",
			$"x-hd2-{hash.ToString("x8", CultureInfo.InvariantCulture)}",
			$"Unknown ({hash.ToString("x8", CultureInfo.InvariantCulture)})");
	}
}

public sealed record GameDataStringTableIssue(
	string Code,
	string Message,
	int? Offset = null);

/// <summary>One parsed GameData <c>strings</c> asset.</summary>
public sealed record GameDataStringTable(
	ulong FileId,
	uint Version,
	uint LanguageHash,
	GameDataLanguageInfo Language,
	IReadOnlyDictionary<uint, string> Entries,
	IReadOnlyList<GameDataStringTableIssue> Issues)
{
	public string Locale => Language.Locale;
	public string LanguageCode => Language.Code;

	public bool TryGet(uint id, out string value)
		=> Entries.TryGetValue(id, out value!);
}

/// <summary>
/// Reads the compact binary format used by Stingray <c>strings</c> assets.
/// </summary>
public static class GameDataStringsReader
{
	public const ulong AssetTypeId = 0x0d972bab10b40fd3;
	public const uint Magic = 0x3e85f3ae;
	private const int HeaderSize = 16;
	private const uint MaximumEntryCount = 1_000_000;
	private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

	public static GameDataStringTable Read(string path, ulong fileId = 0)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		return Read(File.ReadAllBytes(path), fileId);
	}

	public static GameDataStringTable Read(ReadOnlySpan<byte> data, ulong fileId = 0)
	{
		if (data.Length < HeaderSize)
			throw new InvalidDataException("strings asset header is truncated.");

		var magic = BinaryPrimitives.ReadUInt32LittleEndian(data);
		if (magic != Magic)
			throw new InvalidDataException($"Invalid strings asset magic 0x{magic:x8}.");

		var version = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
		var count = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
		var languageHash = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
		if (count > MaximumEntryCount)
			throw new InvalidDataException($"strings asset entry count {count} exceeds the safety limit.");

		var countInt = checked((int)count);
		var idsOffset = HeaderSize;
		var offsetsOffset = checked(idsOffset + checked(countInt * sizeof(uint)));
		var stringDataOffset = checked(offsetsOffset + checked(countInt * sizeof(uint)));
		if (stringDataOffset > data.Length)
			throw new InvalidDataException("strings asset ID/offset tables are truncated.");

		var entries = new Dictionary<uint, string>(countInt);
		var issues = new List<GameDataStringTableIssue>();
		for (var index = 0; index < countInt; index++)
		{
			var id = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(idsOffset + index * 4, 4));
			var offset = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offsetsOffset + index * 4, 4));
			if (offset < stringDataOffset || offset >= data.Length)
			{
				issues.Add(new GameDataStringTableIssue(
					"StringOffsetOutOfRange",
					$"String 0x{id:x8} points to offset 0x{offset:x}, outside the string data region.",
					checked((int)Math.Min(offset, int.MaxValue))));
				continue;
			}

			var textData = data[(int)offset..];
			var terminator = textData.IndexOf((byte)0);
			if (terminator < 0)
			{
				issues.Add(new GameDataStringTableIssue(
					"StringTerminatorMissing",
					$"String 0x{id:x8} at offset 0x{offset:x} has no NUL terminator.",
					checked((int)offset)));
				continue;
			}

			string value;
			try
			{
				value = StrictUtf8.GetString(textData[..terminator]);
			}
			catch (DecoderFallbackException exception)
			{
				issues.Add(new GameDataStringTableIssue(
					"StringUtf8Invalid",
					$"String 0x{id:x8} at offset 0x{offset:x} is not valid UTF-8: {exception.Message}",
					checked((int)offset)));
				continue;
			}

			if (entries.TryGetValue(id, out var existing))
			{
				if (!string.Equals(existing, value, StringComparison.Ordinal))
				{
					issues.Add(new GameDataStringTableIssue(
						"DuplicateStringId",
						$"String ID 0x{id:x8} occurs more than once with different values; the first value was retained.",
						checked((int)offset)));
				}
				continue;
			}

			entries.Add(id, value);
		}

		return new GameDataStringTable(
			fileId,
			version,
			languageHash,
			GameDataLanguages.Resolve(languageHash),
			entries,
			issues);
	}
}
