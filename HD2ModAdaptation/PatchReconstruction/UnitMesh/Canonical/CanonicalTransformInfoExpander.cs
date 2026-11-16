using System.Numerics;

namespace HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;

// Purpose: Imports required source palette bones and their Avatar parent chains into a Canonical target Unit.
public sealed class CanonicalTransformInfoExpander
{
	public UnitMeshModel Expand(
		UnitMeshModel target,
		IEnumerable<(UnitMeshModel Source, UnitRawMeshData SourceMesh)> sources,
		UnitTransformInfo avatar,
		bool includeAvatarSkeleton = false,
		// P4b 合并 VPK Mod 的多个部位 unit：它们的 skin palette 带原版 Avatar 没有的自定义骨——宿主已有就复用，宿主没有就从 donor 自己的 TransformInfo 连同父链搬进来。默认关闭＝旧行为。
		bool allowTargetOwnedBones = false)
	{
		ArgumentNullException.ThrowIfNull(target);
		ArgumentNullException.ThrowIfNull(sources);
		ArgumentNullException.ThrowIfNull(avatar);
		ValidateTransformInfo(target.TransformInfo, "target");
		ValidateTransformInfo(avatar, "Avatar");
		var sourceList = sources.ToList();

		var requiredHashes = (includeAvatarSkeleton
			? avatar.NameHashes
			: sourceList.SelectMany(source => CollectSourcePaletteBoneHashes(source.Source, source.SourceMesh)))
			.Distinct()
			.OrderBy(hash => hash)
			.ToArray();
		if (requiredHashes.Length == 0) return target;
		var expanded = ExpandTransformInfo(
			target.TransformInfo, requiredHashes, avatar,
			allowTargetOwnedBones ? sourceList.Select(source => source.Source.TransformInfo).ToArray() : null);
		return target with { TransformInfo = expanded, TransformNameHashes = expanded.NameHashes };
	}

	private static UnitTransformInfo ExpandTransformInfo(UnitTransformInfo target, IReadOnlyList<uint> requiredHashes, UnitTransformInfo avatar, IReadOnlyList<UnitTransformInfo>? donorOwners)
	{
		var locals = target.LocalTransforms.ToList();
		var matrices = target.Matrices.ToList();
		var entries = target.Entries.ToList();
		var hashes = target.NameHashes.ToList();
		var avatarByHash = avatar.NameHashes.Select((hash, index) => (hash, index)).ToDictionary(item => item.hash, item => item.index);
		var targetByHash = hashes.Select((hash, index) => (hash, index)).ToDictionary(item => item.hash, item => item.index);

		int EnsureHash(uint hash, HashSet<uint> visiting)
		{
			if (!avatarByHash.TryGetValue(hash, out var avatarIndex))
			{
				// 不在 Avatar 里：只有显式允许（donorOwners 非空）才有出路——宿主已有就复用它的节点（不改它）；
				// 宿主没有就从第一个带这根骨的 donor 把它连同父链搬进来；谁都没有仍然照旧抛。
				if (donorOwners is not null)
				{
					if (targetByHash.TryGetValue(hash, out var owned)) return owned;
					var fromDonor = ImportFromDonor(hash, visiting);
					if (fromDonor >= 0) return fromDonor;
				}

				throw new InvalidDataException($"Canonical Avatar TransformInfo does not contain required bone hash 0x{hash:x8}.");
			}
			if (!visiting.Add(hash)) throw new InvalidDataException($"Canonical Avatar TransformInfo contains a parent cycle at bone hash 0x{hash:x8}.");
			var avatarEntry = avatar.Entries[avatarIndex];
			// NOTE(2026-10-16): a self-parented avatar root keeps `avatarIndex` here, which is
			// only correct when the target stores that bone at the same index. Rewriting it to the
			// expanded index looks tempting, but it was tried and it broke 5 existing CLI tests, and
			// it did NOT change the one FK mismatch that motivated the attempt (a donor-side
			// decorative node whose local/parent pair was already inconsistent). Left as-is.
			var parentIndex = avatarIndex;
			if (avatarEntry.ParentIndex != avatarIndex)
			{
				if (avatarEntry.ParentIndex >= avatar.NameHashes.Count) throw new InvalidDataException($"Canonical Avatar bone hash 0x{hash:x8} has invalid parent index {avatarEntry.ParentIndex}.");
				parentIndex = EnsureHash(avatar.NameHashes[avatarEntry.ParentIndex], visiting);
			}
			visiting.Remove(hash);
			if (parentIndex > ushort.MaxValue) throw new InvalidDataException("Expanded Canonical TransformInfo exceeds the uint16 parent-index limit.");

			if (targetByHash.TryGetValue(hash, out var existing))
			{
				// The community SDK exports every selected object against the active
				// Avatar armature. A same-named target bone is therefore not authoritative:
				// replace its transforms and parent link with the Avatar definition too.
				locals[existing] = avatar.LocalTransforms[avatarIndex];
				matrices[existing] = avatar.Matrices[avatarIndex];
				entries[existing] = new UnitTransformEntry(avatarEntry.Increment, checked((ushort)parentIndex));
				return existing;
			}

			if (hashes.Count > ushort.MaxValue) throw new InvalidDataException("Expanded Canonical TransformInfo exceeds the uint16 parent-index limit.");
			var next = hashes.Count;
			hashes.Add(hash);
			locals.Add(avatar.LocalTransforms[avatarIndex]);
			matrices.Add(avatar.Matrices[avatarIndex]);
			entries.Add(new UnitTransformEntry(avatarEntry.Increment, checked((ushort)parentIndex)));
			targetByHash.Add(hash, next);
			return next;
		}

		// 把 donor 自带的自定义骨（Avatar 里没有）按 donor 自己的父链搬进目标。父节点可能是 Avatar 骨、宿主已有骨或另一根 donor 自定义骨，
		// 一律回到 EnsureHash 解析；donor 里的根（自己当自己的父）不会走到这里（根一定在 Avatar 里）。
		int ImportFromDonor(uint hash, HashSet<uint> visiting)
		{
			foreach (var donor in donorOwners!)
			{
				var index = IndexOf(donor, hash);
				if (index < 0) continue;
				if (!visiting.Add(hash)) throw new InvalidDataException($"Donor TransformInfo contains a parent cycle at bone hash 0x{hash:x8}.");
				var entry = donor.Entries[index];
				if (entry.ParentIndex == index || entry.ParentIndex >= donor.NameHashes.Count)
					throw new InvalidDataException($"Donor custom bone 0x{hash:x8} has no usable parent in its own TransformInfo.");
				var parentIndex = EnsureHash(donor.NameHashes[entry.ParentIndex], visiting);
				visiting.Remove(hash);
				if (hashes.Count > ushort.MaxValue) throw new InvalidDataException("Expanded Canonical TransformInfo exceeds the uint16 parent-index limit.");
				var next = hashes.Count;
				hashes.Add(hash);
				locals.Add(donor.LocalTransforms[index]);
				matrices.Add(donor.Matrices[index]);
				entries.Add(new UnitTransformEntry(entry.Increment, checked((ushort)parentIndex)));
				targetByHash.Add(hash, next);
				return next;
			}

			return -1;
		}

		foreach (var hash in requiredHashes) EnsureHash(hash, []);
		return new UnitTransformInfo(target.Reserved0, target.Reserved1, target.Reserved2, locals, matrices, entries, hashes);
	}

	private static int IndexOf(UnitTransformInfo info, uint hash)
	{
		for (var index = 0; index < info.NameHashes.Count; index++)
			if (info.NameHashes[index] == hash) return index;
		return -1;
	}

	private static IReadOnlyList<uint> CollectSourcePaletteBoneHashes(UnitMeshModel source, UnitRawMeshData mesh)
	{
		if (mesh.LodIndex < 0 || mesh.LodIndex >= source.BoneInfos.Count) return Array.Empty<uint>();
		var palette = source.BoneInfos[mesh.LodIndex].RealIndices;
		var hashes = new List<uint>(palette.Count);
		foreach (var transformIndex in palette)
		{
			if (transformIndex >= source.TransformNameHashes.Count)
				throw new InvalidDataException("A source Canonical BoneInfo real index is absent from TransformInfo.");
			hashes.Add(source.TransformNameHashes[(int)transformIndex]);
		}
		return hashes;
	}

	private static void ValidateTransformInfo(UnitTransformInfo info, string role)
	{
		var count = info.NameHashes.Count;
		if (info.LocalTransforms.Count != count || info.Matrices.Count != count || info.Entries.Count != count)
			throw new InvalidDataException($"The {role} TransformInfo arrays have inconsistent counts.");
		if (info.NameHashes.Distinct().Count() != count)
			throw new InvalidDataException($"The {role} TransformInfo contains duplicate name hashes.");
		foreach (var matrix in info.Matrices)
			if (matrix.Values.Count != 16 || matrix.Values.Any(value => !float.IsFinite(value)))
				throw new InvalidDataException($"The {role} TransformInfo contains an invalid matrix.");
	}
}