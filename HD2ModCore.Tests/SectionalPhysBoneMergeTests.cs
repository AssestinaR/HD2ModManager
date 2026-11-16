using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using HD2ModCore.Infrastructure.Sectional;
using Xunit.Abstractions;
using AdaptKey = HD2ModAdaptation.PatchReconstruction.AssetKey;

namespace HD2ModCore.Tests;

/// <summary>
/// S1：合并 unit 时物理骨不能丢（规划 §9）。物理容器按骨名匹配，所以判据是
/// 「来源 unit 节点表里的物理骨，合并结果的节点表里都有」，以及产物里的容器完整。
/// 真数据由环境变量给出（<c>HD2_TEST_SECTIONAL_MOD</c> = 带 <c>sectional/</c> 与 <c>physbone-rig.bin</c> 的 VPK Mod，
/// <c>HD2_TEST_GAME_DATA</c>，编译那条还要 <c>HD2_TEST_MANAGER_ROOT</c>）；缺样本时静默跳过。
/// </summary>
public sealed class SectionalPhysBoneMergeTests(ITestOutputHelper output)
{
	private static UnitMeshModel Model(params uint[] nodes)
		=> ((UnitMeshModel)RuntimeHelpers.GetUninitializedObject(typeof(UnitMeshModel))) with { TransformNameHashes = nodes };

	[Fact]
	public void SelfCheckReportsOnlyRigBonesTheMergedUnitLost()
	{
		var rig = new HashSet<uint> { 10, 11 };
		var source = Model(1, 2, 10, 11, 99);
		Assert.Empty(SectionalPhysBoneRig.FindMissing([source], rig, Model(1, 2, 10, 11)));
		Assert.Equal([11u], SectionalPhysBoneRig.FindMissing([source], rig, Model(1, 2, 10)));
		// 不属于 rig 的自定义节点（99）丢了不算：它没有物理。
		Assert.Empty(SectionalPhysBoneRig.FindMissing([source], rig, Model(10, 11)));
		// 没有物理容器（空 rig）：永远没有缺失，没有物理的 Mod 合并结果与以前一致。
		Assert.Empty(SectionalPhysBoneRig.FindMissing([source], new HashSet<uint>(), Model(1)));
	}

	/// <summary>按协议手搭一个只含索引的容器（够 <see cref="SectionalPhysBoneRig"/> 读；不算摘要）。</summary>
	private static byte[] BuildContainer(params (string Name, byte[] Data)[] entries)
	{
		var index = entries.Sum(e => 0x38 + Encoding.UTF8.GetByteCount(e.Name));
		var data = 48 + index;
		var all = new byte[data + entries.Sum(e => e.Data.Length) + 56];
		"HD2PBX01"u8.CopyTo(all);
		BinaryPrimitives.WriteUInt64LittleEndian(all.AsSpan(0x18), 48);
		BinaryPrimitives.WriteUInt32LittleEndian(all.AsSpan(0x28), (uint)entries.Length);
		var record = 48;
		foreach (var (name, bytes) in entries)
		{
			var nameBytes = Encoding.UTF8.GetBytes(name);
			BinaryPrimitives.WriteUInt16LittleEndian(all.AsSpan(record), (ushort)nameBytes.Length);
			BinaryPrimitives.WriteUInt64LittleEndian(all.AsSpan(record + 4), (ulong)data);
			BinaryPrimitives.WriteUInt64LittleEndian(all.AsSpan(record + 0x0C), (ulong)bytes.Length);
			nameBytes.CopyTo(all, record + 0x38);
			bytes.CopyTo(all, data);
			record += 0x38 + nameBytes.Length;
			data += bytes.Length;
		}

		return all;
	}

	[Fact]
	public async Task RigIsReadBackFromTheContainerByBoneName()
	{
		var rig = new byte[32 + 148 + 20];
		"HD2SRG01"u8.CopyTo(rig);
		BinaryPrimitives.WriteUInt32LittleEndian(rig.AsSpan(8), 2);
		BinaryPrimitives.WriteUInt32LittleEndian(rig.AsSpan(12), 1);
		BinaryPrimitives.WriteUInt32LittleEndian(rig.AsSpan(16), 32 + 148);
		BinaryPrimitives.WriteUInt32LittleEndian(rig.AsSpan(24), 148);
		BinaryPrimitives.WriteUInt32LittleEndian(rig.AsSpan(32 + 8), 0);
		Encoding.UTF8.GetBytes("Hair_a").CopyTo(rig, 32 + 148);
		var container = BuildContainer(("other.bin", [1, 2, 3]), (PhysBoneCarrier.RigRecordName, rig));
		Assert.Equal(rig, SectionalPhysBoneRig.ReadRigRecord(container));
		Assert.Equal(
			[(uint)(HD2GameItemAssets.StingrayHash.Murmur64A("Hair_a") >> 32)],
			SectionalPhysBoneRig.ParseBoneHashes(rig));
		Assert.Null(SectionalPhysBoneRig.ReadRigRecord(BuildContainer(("other.bin", [1]))));
		var missing = Path.Combine(Path.GetTempPath(), "hd2-no-such-patch-" + Guid.NewGuid().ToString("N") + ".patch_0");
		Assert.Empty(await SectionalPhysBoneRig.ReadBoneHashesAsync(missing, default));   // 没有 gpu_resources：空集合
	}

	private static (string Mod, string Game)? Locate()
	{
		var mod = Environment.GetEnvironmentVariable("HD2_TEST_SECTIONAL_MOD");
		var game = Environment.GetEnvironmentVariable("HD2_TEST_GAME_DATA");
		if (string.IsNullOrWhiteSpace(mod) || string.IsNullOrWhiteSpace(game) || !Directory.Exists(game)) return null;
		if (!File.Exists(Path.Combine(mod, "sectional", "physbone-rig.bin"))) return null;
		return (mod, game);
	}

	/// <summary>rig 里每根骨的名字哈希 → 名字（骨名哈希 = 64 位 Stingray 哈希的高 32 位）。</summary>
	private static Dictionary<uint, string> ReadRigBones(string rigPath)
	{
		var rig = File.ReadAllBytes(rigPath);
		var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(rig.AsSpan(12));
		var nameTable = (int)BinaryPrimitives.ReadUInt32LittleEndian(rig.AsSpan(16));
		var recordSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(rig.AsSpan(24));
		var bones = new Dictionary<uint, string>();
		for (var i = 0; i < count; i++)
		{
			var offset = nameTable + (int)BinaryPrimitives.ReadUInt32LittleEndian(rig.AsSpan(32 + i * recordSize + 8));
			var name = Encoding.UTF8.GetString(rig, offset, Array.IndexOf(rig, (byte)0, offset) - offset);
			bones[(uint)(HD2GameItemAssets.StingrayHash.Murmur64A(name) >> 32)] = name;
		}

		return bones;
	}

	private static string Id(AdaptKey key) => key.FileId.ToString("x16", CultureInfo.InvariantCulture);

	[Fact]
	public async Task MergedUnitKeepsEveryRigBoneOfItsSources()
	{
		if (Locate() is not { } located) return;
		var patch = Directory.EnumerateFiles(Path.Combine(located.Mod, "sectional"), "*.patch_0").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).First();
		var rigBones = ReadRigBones(Path.Combine(located.Mod, "sectional", "physbone-rig.bin"));
		var scanned = await new HD2ModAdaptation.PatchReconstruction.PatchTocScanner().ScanEntriesAsync(patch, default);
		var reader = new PatchUnitMeshReader();
		var units = new Dictionary<string, UnitMeshModel>();
		foreach (var entry in scanned.Where(e => e.AssetKey.TypeId == PatchUnitMeshReader.UnitTypeId).OrderBy(e => e.AssetKey.FileId))
			units[Id(entry.AssetKey)] = (await reader.ReadAsync(entry, scanned, PatchUnitDependencyPolicy.RequirePatchLocalComposite, default)).Model;
		var ids = units.Keys.Order().ToArray();
		if (ids.Length < 3) return;
		var avatar = await SectionalUnitMerger.ReadAvatarAsync(located.Game, default);

		// 全部合并（CE-67 躯干合并那类）、去掉头合并（头不并进躯干）、神射手式的「肩」不在来源里，用后两件合并代替小范围。
		foreach (var set in new[] { ids, [.. ids.Skip(1)], [.. ids.Skip(2).Take(2)] })
		{
			var merged = await SectionalUnitMerger.MergeAsync(patch, new SectionalUnitMergeSpec(set), avatar);
			var nodes = new UnitMeshReader().Read(merged.TocData, merged.GpuResourceData).TransformNameHashes.ToHashSet();
			var expected = set.SelectMany(id => units[id].TransformNameHashes).Where(rigBones.ContainsKey).Distinct().ToArray();
			var lost = expected.Where(hash => !nodes.Contains(hash)).Select(hash => rigBones[hash]).ToArray();
			output.WriteLine($"merge[{set.Length}] rigBones in sources={expected.Length}, in merged={expected.Count(nodes.Contains)}, lost={lost.Length}");
			Assert.Empty(lost);
			Assert.DoesNotContain(merged.Warnings, warning => warning.Contains("自定义/物理骨"));
		}
	}

	/// <summary>真编 CE-67 与神射手：产物里的容器完整，每个合并承载件的节点表带齐它所有来源的物理骨。</summary>
	[Theory]
	[InlineData(4081083564u)]
	[InlineData(3970674111u)]
	public async Task CompiledMergeCarriesTheWholeContainerAndEveryRigBone(uint item)
	{
		if (Locate() is not { } located) return;
		var managerRoot = Environment.GetEnvironmentVariable("HD2_TEST_MANAGER_ROOT");
		if (string.IsNullOrWhiteSpace(managerRoot) || !File.Exists(Path.Combine(managerRoot, "data", "indexes", "asset-index.sqlite"))) return;

		var root = Path.Combine(Path.GetTempPath(), "hd2-physbone-tests", Guid.NewGuid().ToString("N"));
		var copy = Path.Combine(root, "mod");
		try
		{
			foreach (var directory in Directory.EnumerateDirectories(located.Mod, "*", SearchOption.AllDirectories))
				Directory.CreateDirectory(directory.Replace(located.Mod, copy));
			Directory.CreateDirectory(copy);
			foreach (var file in Directory.EnumerateFiles(located.Mod, "*", SearchOption.AllDirectories))
			{
				var target = file.Replace(located.Mod, copy);
				File.Copy(file, target, true);
				File.SetAttributes(target, FileAttributes.Normal);
			}

			var paths = new StoragePaths(managerRoot);
			var snapshot = await ArmorAuthorityIndexReader.TryReadAsync(paths);
			var kit = snapshot?.Kits.SingleOrDefault(k => k.ItemId == item && k.Kind == GameDataEquipmentKitKind.Armor);
			if (snapshot is null || kit is null) return;
			var sourceUnits = await SectionalCompiler.ReadSourceUnitsAsync(copy, default);
			if (sourceUnits is null) return;
			var plan = SectionalExclusivePlanner.BuildWithMerge(SectionalExclusivePlanner.BuildIndex(snapshot.Kits), sourceUnits, item, headMerge: false);
			if (!plan.Succeeded || !plan.IsMerged) return;

			var result = await SectionalCompiler.CompileAsync(copy, new VpkHostContext(located.Game, paths), kit.ArchiveId, null, false, [], exclusiveItemId: item);
			Assert.True(result.Ok, result.Error);
			Assert.DoesNotContain(result.Warnings, warning => warning.Contains("自定义/物理骨"));

			var rigPath = Path.Combine(copy, "sectional", "physbone-rig.bin");
			var rigBones = ReadRigBones(rigPath);
			var rigLength = (int)new FileInfo(rigPath).Length;
			var sectionalPatch = Directory.EnumerateFiles(Path.Combine(copy, "sectional"), "*.patch_0").Single();
			var overwritePatch = Directory.EnumerateFiles(ModNodePayloadLayout.GetOverwriteDirectory(copy), "*.patch_0").Single();
			Assert.Equal(rigLength, PhysBoneCarrier.ReadRigRecordLength(await File.ReadAllBytesAsync(overwritePatch + ".gpu_resources")));

			var sourceScan = await new HD2ModAdaptation.PatchReconstruction.PatchTocScanner().ScanEntriesAsync(sectionalPatch, default);
			var outScan = await new HD2ModAdaptation.PatchReconstruction.PatchTocScanner().ScanEntriesAsync(overwritePatch, default);
			var reader = new PatchUnitMeshReader();
			foreach (var merge in plan.Merges)
			{
				var expected = new HashSet<uint>();
				foreach (var id in merge.SourceFileIds)
				{
					var entry = sourceScan.Single(e => e.AssetKey.TypeId == PatchUnitMeshReader.UnitTypeId && Id(e.AssetKey) == id);
					var model = (await reader.ReadAsync(entry, sourceScan, PatchUnitDependencyPolicy.RequirePatchLocalComposite, default)).Model;
					foreach (var hash in model.TransformNameHashes.Where(rigBones.ContainsKey)) expected.Add(hash);
				}

				var carrier = outScan.Single(e => e.AssetKey.TypeId == PatchUnitMeshReader.UnitTypeId && e.AssetKey.FileId == merge.CarrierFileId);
				var carried = (await reader.ReadAsync(carrier, outScan, PatchUnitDependencyPolicy.RequirePatchLocalComposite, default)).Model;
				var nodes = carried.TransformNameHashes.ToHashSet();
				output.WriteLine($"item {item} carrier {merge.CarrierFileId:x16}: sources={merge.SourceFileIds.Count} rigBones expected={expected.Count} present={expected.Count(nodes.Contains)}");
				Assert.All(expected, hash => Assert.True(nodes.Contains(hash), $"carrier {merge.CarrierFileId:x16} 缺物理骨 {rigBones[hash]}"));
			}
		}
		finally
		{
			try { Directory.Delete(root, true); } catch (IOException) { }
		}
	}
}
