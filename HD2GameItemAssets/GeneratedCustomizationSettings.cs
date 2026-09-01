using System.Buffers.Binary;
using System.Text;

namespace HD2GameItemAssets;

/// <summary>
/// A verified generated-customization entry that can be joined to an Archive
/// through <see cref="GameDataHashLookupReader"/> and named through a strings
/// table. The source does not imply that every referenced Archive is a full
/// weapon or vehicle; some are component, skin, optic, or attachment Archives.
/// </summary>
public sealed record GameDataCustomizationArchiveReference(
	uint ItemId,
	ulong ArchivePathHash,
	uint NameUpperStringId,
	uint NameCasedStringId,
	string? DebugName,
	string? FallbackDisplayName,
	string Source);

/// <summary>Reads generated weapon customization entries from decoded GameData.</summary>
public static class GeneratedWeaponCustomizationSettingsReader
{
	public const uint RecordTypeId = 0x1e604234;
	private const int HeaderSize = 16;
	private const int ItemSize = 88;

	public static IReadOnlyList<GameDataCustomizationArchiveReference> Read(ReadOnlySpan<byte> data)
	{
		var result = new List<GameDataCustomizationArchiveReference>();
		foreach (var record in GameDataDataLibraryReader.Read(data).Where(record => record.TypeId == RecordTypeId))
		{
			var payload = record.Payload.AsSpan();
			if (payload.Length < HeaderSize)
			{
				throw new InvalidDataException($"Weapon customization record {record.Index} is missing its item-array header.");
			}

			var itemsOffset = ReadUInt64(payload, 0);
			var itemsCount = ReadUInt64(payload, sizeof(ulong));
			if (itemsOffset > int.MaxValue || itemsCount > int.MaxValue
				|| itemsOffset > (ulong)payload.Length
				|| itemsCount > (ulong)((payload.Length - checked((int)itemsOffset)) / ItemSize))
			{
				throw new InvalidDataException($"Weapon customization record {record.Index} has an invalid item array.");
			}

			for (var itemIndex = 0; itemIndex < checked((int)itemsCount); itemIndex++)
			{
				var item = payload.Slice(checked((int)itemsOffset) + itemIndex * ItemSize, ItemSize);
				var debugOffset = ReadUInt64(item, 0);
				var itemId = ReadUInt32(item, 8);
				var nameUpperId = ReadUInt32(item, 12);
				var nameCasedId = ReadUInt32(item, 16);
				var archivePathHash = ReadUInt64(item, 32);
				if (archivePathHash == 0 || (nameUpperId == 0 && nameCasedId == 0))
				{
					continue;
				}

				var debugName = TryReadUtf8Z(payload, debugOffset);
				result.Add(new GameDataCustomizationArchiveReference(
					itemId,
					archivePathHash,
					nameUpperId,
					nameCasedId,
					debugName,
					debugName,
					"GameData.generated_weapon_customization_settings"));
			}
		}

		return result;
	}

	private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset)
		=> BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, sizeof(uint)));

	private static ulong ReadUInt64(ReadOnlySpan<byte> data, int offset)
		=> BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, sizeof(ulong)));

	internal static string? TryReadUtf8Z(ReadOnlySpan<byte> data, ulong offset)
	{
		if (offset == ulong.MaxValue || offset > int.MaxValue || offset >= (ulong)data.Length)
		{
			return null;
		}

		var bytes = data[checked((int)offset)..];
		var terminator = bytes.IndexOf((byte)0);
		if (terminator < 0)
		{
			return null;
		}

		try
		{
			var text = new UTF8Encoding(false, true).GetString(bytes[..terminator]);
			return string.IsNullOrWhiteSpace(text) ? null : text;
		}
		catch (DecoderFallbackException)
		{
			return null;
		}
	}
}

/// <summary>Reads the supported vehicle/customization entries from decoded GameData.</summary>
public static class GeneratedUnitCustomizationSettingsReader
{
	public const uint RecordTypeId = 0xa2ba274a;
	private const int HeaderSize = 0x90;
	private const int EntrySize = 0x40;
	private const int EntryTablePointerOffset = 0x28;
	private const int EntryCountOffset = 0x30;
	private const int EntryTableHeaderSize = 0x18;
	private const int SourceNamePointerOffset = 0x18;

	public static IReadOnlyList<GameDataCustomizationArchiveReference> Read(ReadOnlySpan<byte> data)
	{
		var result = new List<GameDataCustomizationArchiveReference>();
		foreach (var record in GameDataDataLibraryReader.Read(data).Where(record => record.TypeId == RecordTypeId))
		{
			var payload = record.Payload.AsSpan();
			if (payload.Length < HeaderSize)
			{
				continue;
			}

			var sourceName = GeneratedWeaponCustomizationSettingsReader.TryReadUtf8Z(
				payload,
				ReadUInt64(payload, SourceNamePointerOffset));
			// Only the explicitly named customization subtype has the 0x40 entry
			// layout below. Other records share the outer type but contain an
			// incompatible layout, so they must be ignored rather than guessed.
			if (sourceName is null || !sourceName.StartsWith("nui_customization_type_", StringComparison.Ordinal))
			{
				continue;
			}

			var tablePointer = ReadUInt64(payload, EntryTablePointerOffset);
			var entryCount = ReadUInt64(payload, EntryCountOffset);
			if (tablePointer > int.MaxValue || entryCount == 0 || entryCount > 256)
			{
				continue;
			}

			if (tablePointer > (ulong)(int.MaxValue - EntryTableHeaderSize))
			{
				continue;
			}

			var entriesOffset = checked((int)tablePointer) + EntryTableHeaderSize;
			if (entriesOffset < 0 || entriesOffset > payload.Length
				|| entryCount > (ulong)((payload.Length - entriesOffset) / EntrySize))
			{
				continue;
			}

			for (var itemIndex = 0; itemIndex < checked((int)entryCount); itemIndex++)
			{
				var item = payload.Slice(entriesOffset + itemIndex * EntrySize, EntrySize);
				var archivePathHash = ReadUInt64(item, 0x38);
				var nameCasedId = ReadUInt32(item, 0x30);
				if (archivePathHash == 0 || nameCasedId == 0)
				{
					continue;
				}

				var rawItemId = ReadUInt64(item, 0);
				if (rawItemId > uint.MaxValue)
				{
					// The public model intentionally exposes the customization item ID
					// as uint. Never silently truncate a malformed/newer 64-bit value.
					continue;
				}

				result.Add(new GameDataCustomizationArchiveReference(
					(uint)rawItemId,
					archivePathHash,
					0,
					nameCasedId,
					GeneratedWeaponCustomizationSettingsReader.TryReadUtf8Z(payload, ReadUInt64(item, 8)),
					GeneratedWeaponCustomizationSettingsReader.TryReadUtf8Z(payload, ReadUInt64(item, 0x28)),
					$"GameData.generated_unit_customization_settings:{sourceName}"));
			}
		}

		return result;
	}

	private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset)
		=> BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, sizeof(uint)));

	private static ulong ReadUInt64(ReadOnlySpan<byte> data, int offset)
		=> BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, sizeof(ulong)));
}
