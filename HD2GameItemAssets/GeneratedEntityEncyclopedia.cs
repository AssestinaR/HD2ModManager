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
	/// <summary>
	/// 这一轮是用**推导出来的**布局读的表（而不是命中验证过的常量），格式
	/// <c>表名 I=… C=… (payload …)</c>。空表示全部命中常量。
	/// 出现非空值说明游戏更新改了表长——数据仍然可用，但应该把常量同步过来。
	/// </summary>
	public IReadOnlyList<string> DerivedLayouts { get; init; } = Array.Empty<string>();
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

	// 下面这组是**验证过的当前布局**。公开出来是为了让测试夹具与解析器共用一份数字——
	// 上一次"每个消费者各存一份 96"就是这么把件表解析搞坏的。
	// 游戏更新后这里对不上时，ResolveLayout 会从数据自己推导并校验，不会整块失效。
	public const int EntitySettingsEntrySize = 32;
	// 实测 2026-10-22 的 generated_entities.dl_bin：表长仍是 4096，列表区正好收在 payload 末尾
	// （4096×32 + 列表 = 185172）。改成 5120 会有 1024 条记录的 offset/count 越界。
	public const int EntitySettingsEntryCount = 4096;
	public const int ComponentIndexDataSize = 16;
	// 2026-10-22 更新的实测值（旧值 1314/657、3334/1668、674/338）。
	// 分段判据：索引项是 eid(u64)|componentIndex(u32)|0(u32)，组件区从 IndexCount×16 开始。
	// 实测每个实体的 componentIndex 唯一占一个组件槽：675/675、1719/1720、351/352，
	// 这种 1:1 覆盖是分段正确的决定性证据。
	public const int EncyclopediaIndexCount = 1350;
	public const int EncyclopediaComponentCount = 675;
	public const int EncyclopediaComponentSize = 56;
	public const int UnitIndexCount = 3438;
	public const int UnitComponentCount = 1720;
	public const int UnitComponentSize = 136;
	public const int EquipmentIndexCount = 702;
	public const int EquipmentComponentCount = 352;
	public const int EquipmentComponentSize = 232;
	public const int EquipmentTypeOffset = 128;
	public const int EncyclopediaNameFieldCount = 11;
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

		var entitySettings = FindPayload(instances, GeneratedEntityTypeIds.EntitySettingsHashmap, "EntitySettingsHashmap")
			?? throw new InvalidDataException("EntitySettingsHashmap instance was not found.");
		if (entitySettings.Length < EntitySettingsEntryCount * EntitySettingsEntrySize)
		{
			throw new InvalidDataException($"EntitySettingsHashmap payload is truncated: expected at least {EntitySettingsEntryCount * EntitySettingsEntrySize} bytes, got {entitySettings.Length}.");
		}

		var derivedLayouts = new List<string>();
		var encyclopediaPayload = FindPayload(instances, GeneratedEntityTypeIds.EncyclopediaEntryComponentData, "EncyclopediaEntryComponentData")
			?? throw new InvalidDataException("EncyclopediaEntryComponentData instance was not found.");
		var encyclopediaLayout = ResolveLayout(encyclopediaPayload, EncyclopediaIndexCount, EncyclopediaComponentCount, EncyclopediaComponentSize, "EncyclopediaEntryComponentData", derivedLayouts);

		var unitPayload = FindPayload(instances, GeneratedEntityTypeIds.UnitComponentData, "UnitComponentData")
			?? throw new InvalidDataException("UnitComponentData instance was not found.");
		var unitLayout = ResolveLayout(unitPayload, UnitIndexCount, UnitComponentCount, UnitComponentSize, "UnitComponentData", derivedLayouts);

		var equipmentPayload = FindPayload(instances, GeneratedEntityTypeIds.EquipmentComponentData, "EquipmentComponentData");
		TableLayout? equipmentLayout = equipmentPayload is null
			? null
			: ResolveLayout(equipmentPayload, EquipmentIndexCount, EquipmentComponentCount, EquipmentComponentSize, "EquipmentComponentData", derivedLayouts);

		var entityComponentTypes = ReadEntityComponentTypes(entitySettings, componentTypes);
		var entityToUnit = ReadUnitPaths(unitPayload, unitLayout);
		var names = ReadEncyclopediaNames(encyclopediaPayload, encyclopediaLayout);
		var equipmentTypes = equipmentPayload is null || equipmentLayout is not { } equipmentResolved
			? new Dictionary<ulong, uint>()
			: ReadEquipmentTypes(equipmentPayload, equipmentResolved);

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
			instances.Select(instance => instance.Version).Distinct().Order().ToArray())
		{
			DerivedLayouts = derivedLayouts,
		};
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

	/// <summary>
	/// 按 typeId 取唯一实例的 payload；同一 typeId 出现多次时拒绝，不猜。
	/// 空 payload 的实例是组件登记用的占位（EntitySettingsHashmap 里靠 index 引用它们），
	/// 不是表，所以这里跳过——真正的表不可能是 0 字节。
	/// </summary>
	private static byte[]? FindPayload(IReadOnlyList<Instance> instances, uint typeId, string description)
	{
		var matches = instances
			.Where(instance => instance.Type == typeId && instance.Payload.Length != 0)
			.Select(instance => instance.Payload)
			.ToArray();
		return matches.Length switch
		{
			0 => null,
			1 => matches[0],
			_ => throw new InvalidDataException($"{description} has {matches.Length} instances; the data layout is ambiguous."),
		};
	}

	/// <summary>表布局：索引区 <c>IndexCount</c> 项 × 16 字节，其后是 <c>ComponentCount</c> 个组件。</summary>
	private readonly record struct TableLayout(int IndexCount, int ComponentCount);

	/// <summary>
	/// 先按验证过的常量算期望长度；对上就用常量，对不上就自己推导并校验。
	/// 推导失败才抛——这样游戏更新不会让整块事实静默消失。
	/// </summary>
	private static TableLayout ResolveLayout(
		byte[] payload,
		int indexCount,
		int componentCount,
		int componentSize,
		string description,
		List<string> derivedLayouts)
	{
		var expected = checked(indexCount * ComponentIndexDataSize + componentCount * componentSize);
		if (payload.Length == expected)
		{
			return new TableLayout(indexCount, componentCount);
		}

		var derived = DeriveLayout(payload, componentSize)
			?? throw new InvalidDataException(
				$"{description} payload size {payload.Length} does not match the validated layout {expected}, and no alternative split could be derived.");
		derivedLayouts.Add($"{description} I={derived.IndexCount} C={derived.ComponentCount} (payload {payload.Length}, expected {expected})");
		return derived;
	}

	/// <summary>
	/// 从数据本身推出 (索引项数, 组件数)。
	/// 索引项形状是 <c>eid(u64) | componentIndex(u32) | 0(u32)</c>，组件区紧接其后。
	/// 先从 0 往前扫到第一个形状不符的项，再从那里往回退，找第一个让组件区整除且通过
	/// <see cref="IsValidLayout"/> 的长度。
	/// </summary>
	private static TableLayout? DeriveLayout(byte[] payload, int componentSize)
	{
		var boundary = 0;
		while (checked((boundary + 1) * ComponentIndexDataSize) <= payload.Length)
		{
			var offset = boundary * ComponentIndexDataSize;
			var entityId = ReadUInt64(payload, offset);
			var componentIndex = ReadUInt32(payload, offset + 8);
			if (ReadUInt32(payload, offset + 12) != 0 || (entityId == 0 && componentIndex != 0))
			{
				break;
			}

			boundary++;
		}

		for (var indexCount = boundary; indexCount > 0; indexCount--)
		{
			var remaining = payload.Length - indexCount * ComponentIndexDataSize;
			if (remaining <= 0 || remaining % componentSize != 0)
			{
				continue;
			}

			var componentCount = remaining / componentSize;
			if (IsValidLayout(payload, indexCount, componentCount))
			{
				return new TableLayout(indexCount, componentCount);
			}
		}

		return null;
	}

	/// <summary>
	/// 布局自洽性校验：索引项填充为 0、componentIndex 落在组件区内、且**不被两个实体共用**。
	/// 最后一条是实测出来的强判据——每个实体唯一占一个组件槽，用掉的槽数等于实体数。
	/// </summary>
	private static bool IsValidLayout(byte[] payload, int indexCount, int componentCount)
	{
		var used = new HashSet<uint>();
		for (var index = 0; index < indexCount; index++)
		{
			var offset = index * ComponentIndexDataSize;
			var entityId = ReadUInt64(payload, offset);
			var componentIndex = ReadUInt32(payload, offset + 8);
			if (ReadUInt32(payload, offset + 12) != 0)
			{
				return false;
			}

			if (entityId == 0)
			{
				if (componentIndex != 0)
				{
					return false;
				}

				continue;
			}

			if (componentIndex >= (uint)componentCount || !used.Add(componentIndex))
			{
				return false;
			}
		}

		return used.Count != 0 && componentCount - used.Count <= 1;
	}

	private static Dictionary<ulong, ulong> ReadUnitPaths(byte[] payload, TableLayout layout)
	{
		var result = new Dictionary<ulong, ulong>();
		var componentsOffset = checked(layout.IndexCount * ComponentIndexDataSize);
		for (var index = 0; index < layout.IndexCount; index++)
		{
			var indexOffset = checked(index * ComponentIndexDataSize);
			var entityId = ReadUInt64(payload, indexOffset);
			if (entityId == 0)
			{
				continue;
			}

			var componentIndex = ReadUInt32(payload, indexOffset + 8);
			if (componentIndex >= layout.ComponentCount)
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

	private static Dictionary<ulong, uint> ReadEquipmentTypes(byte[] payload, TableLayout layout)
	{
		var result = new Dictionary<ulong, uint>();
		var componentsOffset = checked(layout.IndexCount * ComponentIndexDataSize);
		for (var index = 0; index < layout.IndexCount; index++)
		{
			var indexOffset = checked(index * ComponentIndexDataSize);
			var entityId = ReadUInt64(payload, indexOffset);
			if (entityId == 0)
			{
				continue;
			}

			var componentIndex = ReadUInt32(payload, indexOffset + 8);
			if (componentIndex >= layout.ComponentCount)
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

	private static Dictionary<ulong, IReadOnlyList<uint>> ReadEncyclopediaNames(byte[] payload, TableLayout layout)
	{
		var result = new Dictionary<ulong, IReadOnlyList<uint>>();
		var componentsOffset = checked(layout.IndexCount * ComponentIndexDataSize);
		for (var index = 0; index < layout.IndexCount; index++)
		{
			var indexOffset = checked(index * ComponentIndexDataSize);
			var entityId = ReadUInt64(payload, indexOffset);
			if (entityId == 0)
			{
				continue;
			}

			var componentIndex = ReadUInt32(payload, indexOffset + 8);
			if (componentIndex >= layout.ComponentCount)
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
