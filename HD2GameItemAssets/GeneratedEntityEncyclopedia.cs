using System.Buffers.Binary;

namespace HD2GameItemAssets;

/// <summary>
/// Type IDs used by the generated entity data library. These values are the
/// game's DB hash values, not Murmur asset type IDs.
/// </summary>
public static class GeneratedEntityTypeIds
{
	public const uint EntitySettingsHashmap = 0x80c1ca70;
	public const uint EncyclopediaEntryComponentData = 0x8a40fe2f;
	public const uint UnitComponentData = 0x4437574d;

	public const uint EquipmentComponentData = 0x5357a825;
	public const uint WieldableComponentData = 0x3bf780f6;
	public const uint WeaponDataComponentData = 0x88e4dbb1;
	public const uint WeaponCustomizationComponentData = 0xeba8f3d0;
}

/// <summary>
/// Values stored in <c>EquipmentComponent.EquipmentType</c>. The first group
/// is the game's weapon/equipment family; value 25 is currently used by the
/// directional shield and value 29 by melee equipment.
/// </summary>
public static class GeneratedEquipmentTypeValues
{
	public const uint AssaultRifle = 1;
	public const uint SpecialGrenade = 15;
	public const uint DirectionalShield = 25;
	public const uint Melee = 29;

	public static bool IsWeaponLike(uint? value)
	{
		if (value is not uint actual)
		{
			return false;
		}

		return (actual >= AssaultRifle && actual <= SpecialGrenade)
			|| actual == DirectionalShield
			|| actual == Melee;
	}
}

/// <summary>
/// One named Entity from <c>generated_entities.dl_bin</c>, joined to its Unit
/// and the component types declared by EntitySettingsHashmap.
/// </summary>
public sealed record GeneratedEntityEncyclopediaEntry(
	ulong EntityId,
	ulong UnitPath,
	IReadOnlyList<uint> NameStringIds,
	IReadOnlyList<uint> ComponentTypeIds,
	uint? EquipmentType = null)
{
	public const int DesignationFieldIndex = 9;

	public bool IsExactUnitMatch => EntityId == UnitPath;

	public uint DesignationStringId
		=> NameStringIds.Count > DesignationFieldIndex ? NameStringIds[DesignationFieldIndex] : 0;

	public bool HasComponent(uint typeId)
		=> ComponentTypeIds.Contains(typeId);

	public bool IsWeaponLikeEquipment => GeneratedEquipmentTypeValues.IsWeaponLike(EquipmentType);
}

/// <summary>Parsed, compact facts from the generated entity data library.</summary>
public sealed record GeneratedEntityEncyclopediaCatalog(
	IReadOnlyList<GeneratedEntityEncyclopediaEntry> Entries,
	IReadOnlyList<uint> InstanceVersions)
{
	public IReadOnlyDictionary<ulong, IReadOnlyList<GeneratedEntityEncyclopediaEntry>> ByUnitPath
		=> Entries
			.GroupBy(entry => entry.UnitPath)
			.ToDictionary(
				group => group.Key,
				group => (IReadOnlyList<GeneratedEntityEncyclopediaEntry>)group.ToArray());
}

/// <summary>
/// Reads the generated EntitySettings, UnitComponent and encyclopedia
/// component tables used by the installed game.
///
/// The generated entity file is not a normal data-library file: most records
/// carry a uint32 component index prefix, while the first record does not.
/// This reader validates that envelope explicitly and only retains the three
/// compact tables needed by the manager.
/// </summary>
public static class GeneratedEntityEncyclopediaReader
{
	public const string ParserVersion = "generated-entities-encyclopedia-v1";

	private const int InstanceHeaderSize = 28;
	private const int EntitySettingsEntrySize = 32;
	private const int EntitySettingsEntryCount = 4096;
	private const int ComponentIndexDataSize = 16;
	private const int EncyclopediaIndexCount = 1314;
	private const int EncyclopediaComponentCount = 657;
	private const int EncyclopediaComponentSize = 56;
	private const int UnitIndexCount = 3334;
	private const int UnitComponentCount = 1668;
	private const int UnitComponentSize = 136;
	private const int EquipmentIndexCount = 674;
	private const int EquipmentComponentCount = 338;
	private const int EquipmentComponentSize = 232;
	private const int EquipmentTypeOffset = 128;
	private const int EncyclopediaNameFieldCount = 11;
	private const uint MaximumInstanceCount = 32_768;

	public static GeneratedEntityEncyclopediaCatalog Read(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		return Read(File.ReadAllBytes(path));
	}

	public static GeneratedEntityEncyclopediaCatalog Read(ReadOnlySpan<byte> data)
	{
		var instances = ReadInstances(data);
		var componentTypes = instances
			.Where(instance => instance.Index is not null)
			.GroupBy(instance => instance.Index!.Value)
			.ToDictionary(
				group => group.Key,
				group =>
				{
					var values = group.Select(instance => instance.Type).Distinct().ToArray();
					return values.Length == 1
						? values[0]
						: throw new InvalidDataException($"Generated entity component index {group.Key} maps to multiple types.");
				});

		var entitySettings = FindRequiredPayload(
			instances,
			GeneratedEntityTypeIds.EntitySettingsHashmap,
			payload => payload.Length >= EntitySettingsEntryCount * EntitySettingsEntrySize,
			"EntitySettingsHashmap");
		var encyclopedia = FindRequiredPayload(
			instances,
			GeneratedEntityTypeIds.EncyclopediaEntryComponentData,
			payload => payload.Length == EncyclopediaIndexCount * ComponentIndexDataSize + EncyclopediaComponentCount * EncyclopediaComponentSize,
			"EncyclopediaEntryComponentData");
		var units = FindRequiredPayload(
			instances,
			GeneratedEntityTypeIds.UnitComponentData,
			payload => payload.Length == UnitIndexCount * ComponentIndexDataSize + UnitComponentCount * UnitComponentSize,
			"UnitComponentData");
		var equipment = FindOptionalPayload(
			instances,
			GeneratedEntityTypeIds.EquipmentComponentData,
			payload => payload.Length == EquipmentIndexCount * ComponentIndexDataSize + EquipmentComponentCount * EquipmentComponentSize,
			"EquipmentComponentData");

		var entityComponentTypes = ReadEntityComponentTypes(entitySettings, componentTypes);
		var entityToUnit = ReadUnitPaths(units);
		var names = ReadEncyclopediaNames(encyclopedia);
		var equipmentTypes = equipment is null
			? new Dictionary<ulong, uint>()
			: ReadEquipmentTypes(equipment);

		var entries = new List<GeneratedEntityEncyclopediaEntry>(names.Count);
		foreach (var (entityId, nameStringIds) in names)
		{
			if (!entityToUnit.TryGetValue(entityId, out var unitPath) || unitPath == 0)
			{
				continue;
			}

			entries.Add(new GeneratedEntityEncyclopediaEntry(
				entityId,
				unitPath,
				nameStringIds,
				entityComponentTypes.TryGetValue(entityId, out var types)
					? types
					: Array.Empty<uint>(),
				equipmentTypes.TryGetValue(entityId, out var equipmentType) ? equipmentType : null));
		}

		return new GeneratedEntityEncyclopediaCatalog(
			entries,
			instances.Select(instance => instance.Version).Distinct().Order().ToArray());
	}

	private static Dictionary<ulong, IReadOnlyList<uint>> ReadEntityComponentTypes(
		ReadOnlySpan<byte> payload,
		IReadOnlyDictionary<uint, uint> componentTypes)
	{
		var fixedSize = checked(EntitySettingsEntryCount * EntitySettingsEntrySize);
		if (payload.Length < fixedSize)
		{
			throw new InvalidDataException($"EntitySettingsHashmap payload is truncated: expected at least {fixedSize} bytes, got {payload.Length}.");
		}

		var result = new Dictionary<ulong, IReadOnlyList<uint>>();
		for (var index = 0; index < EntitySettingsEntryCount; index++)
		{
			var offset = checked(index * EntitySettingsEntrySize);
			var entityId = ReadUInt64(payload, offset);
			if (entityId == 0)
			{
				continue;
			}

			var componentsOffset = ReadUInt64(payload, offset + 8);
			var componentsCount = ReadUInt64(payload, offset + 16);
			if (componentsOffset == ulong.MaxValue || componentsCount == 0)
			{
				result[entityId] = Array.Empty<uint>();
				continue;
			}

			if (componentsOffset > int.MaxValue || componentsCount > int.MaxValue
				|| componentsOffset > (ulong)payload.Length
				|| componentsCount > (ulong)((payload.Length - checked((int)componentsOffset)) / sizeof(ushort)))
			{
				throw new InvalidDataException($"Entity 0x{entityId:x16} has an invalid component list offset/count.");
			}

			var types = new HashSet<uint>();
			for (var component = 0; component < checked((int)componentsCount); component++)
			{
				var componentIndex = BinaryPrimitives.ReadUInt16LittleEndian(
					payload.Slice(checked((int)componentsOffset) + component * sizeof(ushort), sizeof(ushort)));
				if (componentTypes.TryGetValue(componentIndex, out var type))
				{
					types.Add(type);
				}
			}

			result[entityId] = types.Order().ToArray();
		}

		return result;
	}

	private static ReadOnlySpan<byte> FindRequiredPayload(
		IReadOnlyList<Instance> instances,
		uint typeId,
		Func<byte[], bool> accepted,
		string description)
	{
		var payload = FindOptionalPayload(instances, typeId, accepted, description);
		return payload ?? throw new InvalidDataException($"{description} instance was not found with a recognized layout.");
	}

	private static byte[]? FindOptionalPayload(
		IReadOnlyList<Instance> instances,
		uint typeId,
		Func<byte[], bool> accepted,
		string description)
	{
		var matches = instances
			.Where(instance => instance.Type == typeId && accepted(instance.Payload))
			.Select(instance => instance.Payload)
			.ToArray();
		return matches.Length switch
		{
			0 => null,
			1 => matches[0],
			_ => throw new InvalidDataException($"{description} has {matches.Length} matching instances; the data layout is ambiguous.")
		};
	}

	private static Dictionary<ulong, ulong> ReadUnitPaths(ReadOnlySpan<byte> payload)
	{
		var expectedSize = checked(UnitIndexCount * ComponentIndexDataSize + UnitComponentCount * UnitComponentSize);
		if (payload.Length != expectedSize)
		{
			throw new InvalidDataException($"UnitComponentData payload size {payload.Length} does not match the validated current layout {expectedSize}.");
		}

		var result = new Dictionary<ulong, ulong>();
		var componentsOffset = checked(UnitIndexCount * ComponentIndexDataSize);
		for (var index = 0; index < UnitIndexCount; index++)
		{
			var indexOffset = checked(index * ComponentIndexDataSize);
			var entityId = ReadUInt64(payload, indexOffset);
			if (entityId == 0)
			{
				continue;
			}

			var componentIndex = ReadUInt32(payload, indexOffset + 8);
			if (componentIndex >= UnitComponentCount)
			{
				throw new InvalidDataException($"Unit component index {componentIndex} is outside the component array.");
			}

			var componentOffset = checked(componentsOffset + checked((int)componentIndex) * UnitComponentSize);
			var unitPath = ReadUInt64(payload, componentOffset);
			if (unitPath == 0)
			{
				continue;
			}

			if (result.TryGetValue(entityId, out var existing) && existing != unitPath)
			{
				throw new InvalidDataException($"Entity 0x{entityId:x16} maps to multiple UnitPath values.");
			}
			result[entityId] = unitPath;
		}

		return result;
	}

	private static Dictionary<ulong, uint> ReadEquipmentTypes(ReadOnlySpan<byte> payload)
	{
		var expectedSize = checked(EquipmentIndexCount * ComponentIndexDataSize + EquipmentComponentCount * EquipmentComponentSize);
		if (payload.Length != expectedSize)
		{
			throw new InvalidDataException($"EquipmentComponentData payload size {payload.Length} does not match the validated current layout {expectedSize}.");
		}

		var result = new Dictionary<ulong, uint>();
		var componentsOffset = checked(EquipmentIndexCount * ComponentIndexDataSize);
		for (var index = 0; index < EquipmentIndexCount; index++)
		{
			var indexOffset = checked(index * ComponentIndexDataSize);
			var entityId = ReadUInt64(payload, indexOffset);
			if (entityId == 0)
			{
				continue;
			}

			var componentIndex = ReadUInt32(payload, indexOffset + 8);
			if (componentIndex >= EquipmentComponentCount)
			{
				throw new InvalidDataException($"Equipment component index {componentIndex} is outside the component array.");
			}

			var componentOffset = checked(componentsOffset + checked((int)componentIndex) * EquipmentComponentSize);
			var equipmentType = ReadUInt32(payload, componentOffset + EquipmentTypeOffset);
			if (result.TryGetValue(entityId, out var existing) && existing != equipmentType)
			{
				throw new InvalidDataException($"Entity 0x{entityId:x16} maps to multiple EquipmentType values.");
			}
			result[entityId] = equipmentType;
		}

		return result;
	}

	private static Dictionary<ulong, IReadOnlyList<uint>> ReadEncyclopediaNames(ReadOnlySpan<byte> payload)
	{
		var expectedSize = checked(EncyclopediaIndexCount * ComponentIndexDataSize + EncyclopediaComponentCount * EncyclopediaComponentSize);
		if (payload.Length != expectedSize)
		{
			throw new InvalidDataException($"EncyclopediaEntryComponentData payload size {payload.Length} does not match the validated current layout {expectedSize}.");
		}

		var result = new Dictionary<ulong, IReadOnlyList<uint>>();
		var componentsOffset = checked(EncyclopediaIndexCount * ComponentIndexDataSize);
		for (var index = 0; index < EncyclopediaIndexCount; index++)
		{
			var indexOffset = checked(index * ComponentIndexDataSize);
			var entityId = ReadUInt64(payload, indexOffset);
			if (entityId == 0)
			{
				continue;
			}

			var componentIndex = ReadUInt32(payload, indexOffset + 8);
			if (componentIndex >= EncyclopediaComponentCount)
			{
				throw new InvalidDataException($"Encyclopedia component index {componentIndex} is outside the component array.");
			}

			var componentOffset = checked(componentsOffset + checked((int)componentIndex) * EncyclopediaComponentSize);
			var stringIds = new uint[EncyclopediaNameFieldCount];
			for (var field = 0; field < stringIds.Length; field++)
			{
				stringIds[field] = ReadUInt32(payload, componentOffset + field * sizeof(uint));
			}

			if (result.TryGetValue(entityId, out var existing)
				&& !existing.SequenceEqual(stringIds))
			{
				throw new InvalidDataException($"Entity 0x{entityId:x16} maps to multiple encyclopedia entries.");
			}
			result[entityId] = stringIds;
		}

		return result;
	}

	private static IReadOnlyList<Instance> ReadInstances(ReadOnlySpan<byte> data)
	{
		var result = new List<Instance>();
		var position = 0;
		var hasIndex = false;
		while (position < data.Length)
		{
			uint? index = null;
			if (hasIndex)
			{
				Ensure(data, position, sizeof(uint), "instance index");
				index = ReadUInt32(data, position);
				position += sizeof(uint);
			}

			Ensure(data, position, InstanceHeaderSize, "instance header");
			var type0 = ReadUInt32(data, position);
			if (!data.Slice(position + 4, 4).SequenceEqual("LDLD"u8))
			{
				throw new InvalidDataException($"Generated entity instance at 0x{position:x} has invalid LDLD magic.");
			}

			var version = ReadUInt32(data, position + 8);
			var type = ReadUInt32(data, position + 12);
			var size = ReadUInt32(data, position + 16);
			var payloadOffset = checked(position + InstanceHeaderSize);
			Ensure(data, payloadOffset, checked((int)size), "instance payload");
			var payload = data.Slice(payloadOffset, checked((int)size)).ToArray();
			result.Add(new Instance(index, type0, type, version, payload));

			var end = checked(payloadOffset + (int)size);
			if (end == data.Length)
			{
				break;
			}

			Ensure(data, end, 8, "next instance marker");
			hasIndex = ReadUInt32(data, end + 4) != 0x444c444c;
			position = end;
			if (result.Count > MaximumInstanceCount)
			{
				throw new InvalidDataException($"Generated entity data contains more than {MaximumInstanceCount} instances.");
			}
		}

		return result;
	}

	private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset)
		=> BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, sizeof(uint)));

	private static ulong ReadUInt64(ReadOnlySpan<byte> data, int offset)
		=> BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, sizeof(ulong)));

	private static void Ensure(ReadOnlySpan<byte> data, int offset, int size, string field)
	{
		if (offset < 0 || size < 0 || offset > data.Length - size)
		{
			throw new InvalidDataException($"Generated entity data is truncated while reading {field} at 0x{offset:x}.");
		}
	}

	private sealed record Instance(uint? Index, uint Type0, uint Type, uint Version, byte[] Payload);
}
