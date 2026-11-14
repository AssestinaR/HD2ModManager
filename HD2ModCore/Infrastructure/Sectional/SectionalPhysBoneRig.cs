using System.Buffers.Binary;
using System.Text;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;

namespace HD2ModCore.Infrastructure.Sectional;

/// <summary>
/// 读 patch 里物理容器（<c>HD2PBX01</c>）的 rig，并核对合并后的 unit 没丢物理骨（规划 §9）。
/// <para>
/// 物理容器每个 Mod 一份、按<b>骨名</b>匹配（运行时 <c>has_node(unit, 骨名)</c>），容器里没有任何东西属于某个 unit，
/// 所以合并 unit 时容器本身不用改；要保证的只有一件事：来源 unit 节点表里的物理骨，合并结果的节点表里都在。
/// 这个类是那件事的独立组件（P4b 合并用，S2 的压缩编译直接复用）。
/// </para>
/// </summary>
internal static class SectionalPhysBoneRig
{
	private const int HeaderSize = 32;

	/// <summary>patch 旁 <c>.gpu_resources</c> 里 rig 的骨名哈希；没有容器或 rig 就返回空集合（没有物理的 Mod 不受任何影响）。</summary>
	internal static async Task<IReadOnlySet<uint>> ReadBoneHashesAsync(string patchPath, CancellationToken cancellationToken)
	{
		var gpu = patchPath + ".gpu_resources";
		byte[]? rig = null;
		if (File.Exists(gpu))
		{
			var container = await PhysBoneCarrier.TryReadContainerFromFileAsync(gpu, cancellationToken).ConfigureAwait(false);
			rig = container is null ? null : ReadRigRecord(container);
		}

		// 有些 Mod 的 sectional patch 没嵌容器，rig 只在旁边的 physbone-rig.bin（部署时才嵌进 Overwrite）；也要查。
		var sibling = Path.Combine(Path.GetDirectoryName(patchPath) ?? string.Empty, "physbone-rig.bin");
		if (rig is null && File.Exists(sibling))
			rig = await File.ReadAllBytesAsync(sibling, cancellationToken).ConfigureAwait(false);
		return rig is null ? new HashSet<uint>() : ParseBoneHashes(rig);
	}

	/// <summary>从完整容器字节里取出 <c>hd2_spring_rig.bin</c> 记录；没有返回 null。</summary>
	internal static byte[]? ReadRigRecord(byte[] container)
	{
		if (container.Length < 48) return null;
		var count = BinaryPrimitives.ReadUInt32LittleEndian(container.AsSpan(0x28));
		var at = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(container.AsSpan(0x18)));
		for (var i = 0; i < count; i++)
		{
			if (at < 0 || at + 0x38 > container.Length) return null;
			var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(container.AsSpan(at));
			var dataAt = BinaryPrimitives.ReadUInt64LittleEndian(container.AsSpan(at + 4));
			var dataLength = BinaryPrimitives.ReadUInt64LittleEndian(container.AsSpan(at + 0x0C));
			if (at + 0x38 + nameLength > container.Length) return null;
			var name = Encoding.UTF8.GetString(container, at + 0x38, nameLength);
			if (name == PhysBoneCarrier.RigRecordName)
				return dataAt + dataLength <= (ulong)container.Length ? container.AsSpan((int)dataAt, (int)dataLength).ToArray() : null;
			at += 0x38 + nameLength;
		}

		return null;
	}

	/// <summary>解析 <c>HD2SRG01</c>：每根骨的名字 → 骨名哈希（64 位 Stingray 哈希的高 32 位，与 unit 节点表同一口径）。</summary>
	internal static IReadOnlySet<uint> ParseBoneHashes(byte[] rig)
	{
		var hashes = new HashSet<uint>();
		if (rig.Length < HeaderSize || !rig.AsSpan(0, 8).SequenceEqual("HD2SRG01"u8)) return hashes;
		var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(rig.AsSpan(12));
		var nameTable = (int)BinaryPrimitives.ReadUInt32LittleEndian(rig.AsSpan(16));
		var recordSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(rig.AsSpan(24));
		for (var i = 0; i < count; i++)
		{
			var record = HeaderSize + i * recordSize;
			if (record + 12 > rig.Length) break;
			var start = nameTable + (int)BinaryPrimitives.ReadUInt32LittleEndian(rig.AsSpan(record + 8));
			if (start < 0 || start >= rig.Length) continue;
			var end = Array.IndexOf(rig, (byte)0, start);
			if (end < 0) continue;
			hashes.Add((uint)(HD2GameItemAssets.StingrayHash.Murmur64A(Encoding.UTF8.GetString(rig, start, end - start)) >> 32));
		}

		return hashes;
	}

	/// <summary>来源节点表里属于 rig 的物理骨，合并结果的节点表里缺了哪些（升序、去重）。</summary>
	internal static IReadOnlyList<uint> FindMissing(
		IEnumerable<UnitMeshModel> sources, IReadOnlySet<uint> rigBones, UnitMeshModel merged)
	{
		if (rigBones.Count == 0) return [];
		var present = merged.TransformNameHashes.ToHashSet();
		return [.. sources
			.SelectMany(source => source.TransformNameHashes)
			.Where(hash => rigBones.Contains(hash) && !present.Contains(hash))
			.Distinct()
			.Order()];
	}
}
